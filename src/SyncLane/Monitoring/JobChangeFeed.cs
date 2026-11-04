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
internal sealed partial class JobChangeFeed(NpgsqlDataSource dataSource, string schema, TimeProvider time, ILogger<JobChangeFeed> logger)
    : IAsyncDisposable
{
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
                await using var connection = await dataSource.OpenConnectionAsync(stopping);
                connection.Notification += OnNotification;

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

    private void OnNotification(object sender, NpgsqlNotificationEventArgs notification)
    {
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Listening for job changes on channel {Channel}.")]
    private partial void LogListening(string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Listening for job changes on channel {Channel} stopped and starts again in {Pause}. Workers poll meanwhile, and live " +
        "views miss what changes until then. A pooler in transaction mode cannot listen: connect directly.")]
    private partial void LogBroken(Exception exception, string channel, TimeSpan pause);
}
