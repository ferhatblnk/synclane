using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Npgsql;
using SyncLane.Storage;

namespace SyncLane.Monitoring;

/// <summary>
/// Listens on the schema's notification channel with one connection per process and hands every announced job change to
/// whoever reads: live views and idle worker slots. The connection is opened on the first reader and kept, and opened
/// again with a growing pause when it breaks. Changes announced while it was broken are not replayed.
/// </summary>
/// <remarks>
/// A pooler in transaction mode accepts <c>LISTEN</c> and never delivers what it hears, without an error, and the server
/// connection it lent keeps the registration: every change sent there afterwards reaches whichever client the pooler
/// lends it to next, which then times out now and again. So each new connection first listens on a channel of its own,
/// which nothing else ever notifies, and waits to hear a probe sent there through <paramref name="sending"/>, the
/// connection everything else writes on. Only a connection that hears it listens on the schema's channel. One that does
/// not stops listening for good, and workers poll.
/// </remarks>
internal sealed partial class JobChangeFeed(
    string listenConnectionString, NpgsqlDataSource sending, string schema, TimeProvider time, ILogger<JobChangeFeed> logger)
    : IAsyncDisposable
{
    /// <summary>How long a new connection waits to hear its probe before it gives up listening.</summary>
    internal TimeSpan ProbePatience { get; init; } = TimeSpan.FromSeconds(5);

    // Unpooled, so a connection that stops listening is closed instead of going back to a pool still registered as a
    // listener, where the server would keep sending to a socket nobody reads.
    private readonly NpgsqlDataSource _listenSource =
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(listenConnectionString) { Pooling = false }.ConnectionString);

    private Probe? _probe;

    /// <summary>Changes a reader may fall behind by before the oldest are dropped, so a slow reader never holds up the rest.</summary>
    internal const int ReaderCapacity = 1000;

    /// <summary>How long the connection waits quietly before it checks that the server is still there.</summary>
    internal static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(1);

    private readonly string _channel = ChangeChannel.For(schema);
    private readonly ConcurrentDictionary<Channel<JobChange>, (string? Queue, string? Owner)> _readers = new();
    private readonly ConcurrentDictionary<Channel<PauseSignal>, byte> _pauseReaders = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task? _listening;
    private TaskCompletionSource _listeningNow = NewSignal();

    /// <summary>Changes from now on, of one queue or owner if given, until <paramref name="cancellationToken"/> ends the reading.</summary>
    public async IAsyncEnumerable<JobChange> ReadAllAsync(
        string? queue, string? owner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = Channel.CreateBounded<JobChange>(
            new BoundedChannelOptions(ReaderCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _readers[reader] = (queue, owner);
        EnsureListening();

        try
        {
            // Sequential: reading one channel in order.
            await foreach (var change in reader.Reader.ReadAllAsync(cancellationToken))
                yield return change;
        }
        finally
        {
            _readers.TryRemove(reader, out _);
        }
    }

    /// <summary>Pauses and resumes as operators set them, on any instance, until <paramref name="cancellationToken"/> ends the reading.</summary>
    public async IAsyncEnumerable<PauseSignal> ReadPausesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = Channel.CreateBounded<PauseSignal>(
            new BoundedChannelOptions(ReaderCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _pauseReaders[reader] = 0;
        EnsureListening();

        try
        {
            // Sequential: reading one channel in order.
            await foreach (var signal in reader.Reader.ReadAllAsync(cancellationToken))
                yield return signal;
        }
        finally
        {
            _pauseReaders.TryRemove(reader, out _);
        }
    }

    /// <summary>Completes once the connection listens, so a caller can make changes knowing they will be seen.</summary>
    internal Task ListeningAsync(CancellationToken cancellationToken)
    {
        EnsureListening();
        return _listeningNow.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_listening is { } listening)
            await listening.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        foreach (var reader in _readers.Keys)
            reader.Writer.TryComplete();
        foreach (var reader in _pauseReaders.Keys)
            reader.Writer.TryComplete();

        await _listenSource.DisposeAsync();
        _stopping.Dispose();
    }

    private void EnsureListening()
    {
        lock (_gate)
            _listening ??= Task.Run(() => ListenAsync(_stopping.Token));
    }

    private async Task ListenAsync(CancellationToken stopping)
    {
        var pause = FirstPause;

        // Sequential: a connection is opened again only after the previous one broke.
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _listenSource.OpenConnectionAsync(stopping);
                connection.Notification += OnNotification;

                var heard = await HearsProbeAsync(connection, stopping);

                // Drops the probe's channel. Behind a pooler it reaches whichever server connection the pooler lends,
                // and clears what an older version may have left listening there.
                await using (var unlisten = new NpgsqlCommand("UNLISTEN *", connection))
                    await unlisten.ExecuteNonQueryAsync(stopping);

                if (!heard)
                {
                    LogDeaf(_channel, ProbePatience);
                    return;
                }

                await using (var listen = new NpgsqlCommand($"LISTEN {_channel}", connection))
                    await listen.ExecuteNonQueryAsync(stopping);

                pause = FirstPause;
                _listeningNow.TrySetResult();
                LogListening(_channel);

                // Sequential: waiting for notifications on one connection.
                while (!stopping.IsCancellationRequested)
                {
                    if (await connection.WaitAsync(KeepAlive, stopping))
                        continue;

                    await using var ping = new NpgsqlCommand("SELECT 1", connection);
                    await ping.ExecuteScalarAsync(stopping);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A new signal for the next connection: changes made before it listens again are not seen.
                if (_listeningNow.Task.IsCompleted)
                    _listeningNow = NewSignal();

                LogBroken(exception, _channel, pause);
                try
                {
                    await Task.Delay(pause, time, stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                pause = pause * 2 < LongestPause ? pause * 2 : LongestPause;
            }
        }
    }

    /// <summary>
    /// Listens on a channel of the probe's own, sends to it on another connection and waits for
    /// <paramref name="connection"/> to hear it. Another connection, because a session hears its own notifications even
    /// behind a pooler, which would hide the problem.
    /// </summary>
    private async Task<bool> HearsProbeAsync(NpgsqlConnection connection, CancellationToken stopping)
    {
        var probe = new Probe(ChangeChannel.ForProbe(Guid.NewGuid()));
        _probe = probe;
        try
        {
            await using (var listen = new NpgsqlCommand($"LISTEN {probe.Channel}", connection))
                await listen.ExecuteNonQueryAsync(stopping);

            await using (var send = sending.CreateCommand(ChangeChannel.Probe))
            {
                send.Parameters.AddWithValue("channel", probe.Channel);
                await send.ExecuteNonQueryAsync(stopping);
            }

            var started = time.GetTimestamp();

            // Sequential: waiting for notifications on one connection.
            while (!probe.Heard.Task.IsCompleted)
            {
                // Whole milliseconds: Npgsql cuts a timeout to them, and a cut to 0 waits forever.
                var left = Math.Ceiling((ProbePatience - time.GetElapsedTime(started)).TotalMilliseconds);
                if (left < 1)
                    break;

                await connection.WaitAsync(TimeSpan.FromMilliseconds(left), stopping);
            }

            return probe.Heard.Task.IsCompleted;
        }
        finally
        {
            _probe = null;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs notification)
    {
        if (_probe is { } probe && notification.Channel == probe.Channel)
        {
            probe.Heard.TrySetResult();
            return;
        }

        if (ReadPause(notification.Payload) is { } pause)
        {
            foreach (var reader in _pauseReaders.Keys)
                reader.Writer.TryWrite(pause);
            return;
        }

        if (Read(notification.Payload) is not { } change)
            return;

        foreach (var (reader, filter) in _readers)
        {
            if ((filter.Queue is null || filter.Queue == change.Queue) && (filter.Owner is null || filter.Owner == change.Owner))
                reader.Writer.TryWrite(change);
        }
    }

    /// <summary>The trigger's announcement as a change, or <c>null</c> for one this version cannot read.</summary>
    internal static JobChange? Read(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return new JobChange
            {
                Id = root.GetProperty("id").GetInt64(),
                Queue = root.GetProperty("queue").GetString()!,
                Owner = root.GetProperty("owner").GetString()!,
                Type = root.GetProperty("type").GetString()!,
                Status = JobText.Status(root.GetProperty("status").GetString()!),
                At = root.GetProperty("at").GetDateTimeOffset()
            };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or SyncLaneException)
        {
            return null;
        }
    }

    /// <summary>A pause or resume announcement, or <c>null</c> for anything else.</summary>
    internal static PauseSignal? ReadPause(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("control", out var control))
                return null;

            var resumed = control.GetString() switch
            {
                "pause" => false,
                "resume" => true,
                _ => (bool?)null
            };

            return resumed is { } isResume
                ? new PauseSignal(root.GetProperty("queue").GetString()!, root.GetProperty("owner").GetString()!, isResume)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Probe(string Channel)
    {
        public TaskCompletionSource Heard { get; } = NewSignal();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Listening for job changes on channel {Channel}.")]
    private partial void LogListening(string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Listening for job changes on channel {Channel} stopped and starts again in {Pause}. Workers poll meanwhile, and live " +
        "views miss what changes until then.")]
    private partial void LogBroken(Exception exception, string channel, TimeSpan pause);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Listening for job changes on channel {Channel} has stopped until the next start: a change sent through ConnectionString " +
        "was not heard within {Patience}. A pooler in transaction mode accepts LISTEN but never delivers what it hears. Set " +
        "ListenConnectionString to a connection straight to the server, or through a pooler in session mode. Until then " +
        "workers look for jobs every PollInterval and live views stay quiet.")]
    private partial void LogDeaf(string channel, TimeSpan patience);
}
