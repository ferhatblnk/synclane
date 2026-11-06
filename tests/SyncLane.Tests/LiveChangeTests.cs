using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncLane.Monitoring;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Job changes announced by the database reach every listening process a moment after they commit.</summary>
public class LiveChangeTests : PostgresTest
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_job_is_announced_when_it_is_created_and_when_its_status_changes_but_not_for_a_page_or_a_heartbeat()
    {
        await using var feed = Feed();
        await feed.ListeningAsync(CancellationToken.None);
        var seen = CollectAsync(feed.ReadAllAsync(null, null, CancellationToken.None), 3);

        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var job = (await LeaseAsync("w", Noon))!;
        await Store.CheckpointAsync(job.Id, "w", "page-2", 10, Noon, Noon.AddMinutes(5), CancellationToken.None);
        await Store.HeartbeatAsync(job.Id, "w", Noon, Noon.AddMinutes(5), CancellationToken.None);
        await Store.CompleteAsync(job.Id, "w", Noon, CancellationToken.None);

        var changes = await seen;
        Assert.Equal([JobStatus.Pending, JobStatus.Running, JobStatus.Completed], changes.Select(change => change.Status));
        Assert.All(changes, change => Assert.Equal((job.Id, Queue, "a", "orders"), (change.Id, change.Queue, change.Owner, change.Type)));
    }

    [PostgresFact]
    public async Task Another_instance_sees_the_changes_in_order_and_only_those_it_asked_for()
    {
        await using var feed = Feed();
        await feed.ListeningAsync(CancellationToken.None);
        var seen = CollectAsync(feed.ReadAllAsync(Queue, "a", CancellationToken.None), 2);

        await EnqueueAsync(Noon, Planned("b", key: "1"));
        await Store.EnqueueAsync("billing", [Planned("a", key: "2")], Noon, CancellationToken.None);
        await EnqueueAsync(Noon, Planned("a", key: "3"));
        await Store.CancelAsync(Queue, "a", "account closed", Noon, CancellationToken.None);

        var changes = await seen;
        Assert.Equal([(JobStatus.Pending, "a"), (JobStatus.Abandoned, "a")], changes.Select(change => (change.Status, change.Owner)));
        Assert.All(changes, change => Assert.Equal(Queue, change.Queue));
    }

    [PostgresFact]
    public async Task A_long_owner_key_never_stops_a_write_and_is_announced_cut_short()
    {
        await using var feed = Feed();
        await feed.ListeningAsync(CancellationToken.None);
        var seen = CollectAsync(feed.ReadAllAsync(null, null, CancellationToken.None), 1);
        var owner = new string('x', 10_000);

        var created = await EnqueueAsync(Noon, Planned(owner, key: "1"));

        Assert.Equal(1, created);
        Assert.Equal(500, Assert.Single(await seen).Owner.Length);
    }

    [PostgresFact]
    public async Task Listening_starts_again_after_the_connection_breaks()
    {
        await using var feed = Feed();
        await feed.ListeningAsync(CancellationToken.None);
        using var reading = new CancellationTokenSource(Patience);
        var changes = feed.ReadAllAsync(null, null, reading.Token).GetAsyncEnumerator(reading.Token);

        await ExecuteAsync(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query = 'LISTEN {Storage.ChangeChannel.For(Schema)}'");

        var next = changes.MoveNextAsync().AsTask();
        var key = 0;
        // Sequential: a job every fifth of a second until one is seen, since changes made while it reconnects are lost.
        while (!next.IsCompleted)
        {
            await EnqueueAsync(Noon, Planned("a", key: $"{key++}"));
            await Task.WhenAny(next, Task.Delay(200, reading.Token));
        }

        Assert.True(await next);
        Assert.Equal(JobStatus.Pending, changes.Current.Status);
        await changes.DisposeAsync();
    }

    [PostgresFact]
    public async Task An_idle_worker_takes_a_new_job_at_once_instead_of_after_its_poll_interval()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.ListenConnectionString = ListenConnectionString;
            options.Schema = Schema;
            options.RunPlanner = false;
            options.PollInterval = TimeSpan.FromHours(1);
            options.AddQueue(Queue, queue => queue.Handle<ProductHandler>("orders"));
        });
        using var host = builder.Build();
        await host.StartAsync();
        await host.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        using var timeout = new CancellationTokenSource(Patience);
        // Sequential: polling the table until the job is done.
        while ((await JobsAsync(JobStatus.Completed)).Count == 0)
            await Task.Delay(50, timeout.Token);

        await host.StopAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [PostgresFact]
    public async Task A_connection_that_cannot_hear_what_is_written_stops_listening_for_good_and_names_the_setting_that_fixes_it()
    {
        // Another database hears nothing written to this one: the same silence a pooler in transaction mode gives.
        var log = new LogLines();
        await using var feed = new JobChangeFeed(AnotherDatabase(), Store.DataSource, Schema, _clock, log.For<JobChangeFeed>())
        {
            ProbePatience = TimeSpan.FromSeconds(1)
        };
        var listening = feed.ListeningAsync(CancellationToken.None);

        var warning = await log.WaitForAsync(LogLevel.Warning, "ListenConnectionString", Patience);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Contains("transaction mode", warning);
        Assert.False(listening.IsCompleted);
        Assert.Single(log.Lines, line => line.Level == LogLevel.Warning);
        Assert.Equal(0L, await ScalarAsync<long>(
            $"SELECT count(*) FROM pg_stat_activity WHERE query = 'LISTEN {Storage.ChangeChannel.For(Schema)}'"));
    }

    [PostgresFact]
    public async Task A_connection_listens_on_the_schemas_channel_only_once_it_has_heard_its_probe()
    {
        // Behind a pooler a LISTEN stays on a server connection, and every change sent there reaches whichever client
        // the pooler lends it to next. So before the probe is heard, nothing sent to the schema's channel may arrive.
        var other = AnotherDatabase();
        var log = new LogLines();
        await using var feed = new JobChangeFeed(other, Store.DataSource, Schema, _clock, log.For<JobChangeFeed>())
        {
            ProbePatience = TimeSpan.FromSeconds(2)
        };
        using var reading = new CancellationTokenSource(Patience);
        var changes = feed.ReadAllAsync(null, null, reading.Token).GetAsyncEnumerator(reading.Token);
        var next = changes.MoveNextAsync().AsTask();

        await using var otherDatabase = NpgsqlDataSource.Create(other);
        // Sequential: polling until the probe listens.
        while (await Scalar<long>(otherDatabase, "SELECT count(*) FROM pg_stat_activity WHERE query LIKE 'LISTEN synclane_probe_%'") == 0)
            await Task.Delay(20, reading.Token);
        await Scalar<object>(otherDatabase,
            $"SELECT pg_notify('{Storage.ChangeChannel.For(Schema)}', json_build_object('id', 1, 'queue', '{Queue}', 'owner', 'a', " +
            "'type', 'orders', 'status', 'pending', 'at', now())::text)");

        await log.WaitForAsync(LogLevel.Warning, "ListenConnectionString", Patience);
        Assert.False(next.IsCompleted);
        await reading.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    private static async Task<T?> Scalar<T>(NpgsqlDataSource source, string statement)
    {
        await using var command = source.CreateCommand(statement);
        return await command.ExecuteScalarAsync() is T value ? value : default;
    }

    [PostgresFact]
    public async Task Waiting_for_the_probe_ends_even_when_less_than_a_millisecond_is_left()
    {
        // Npgsql cuts a wait to whole milliseconds and waits forever on 0; a patience under one millisecond is all remainder.
        var log = new LogLines();
        await using var feed = new JobChangeFeed(AnotherDatabase(), Store.DataSource, Schema, _clock, log.For<JobChangeFeed>())
        {
            ProbePatience = TimeSpan.FromTicks(5_000)
        };
        _ = feed.ListeningAsync(CancellationToken.None);

        await log.WaitForAsync(LogLevel.Warning, "ListenConnectionString", Patience);
    }

    [PoolerFact]
    public async Task Behind_a_pooler_in_transaction_mode_listening_stops_with_a_warning_instead_of_hearing_nothing()
    {
        var log = new LogLines();
        await using var feed = new JobChangeFeed(ConnectionString!, Store.DataSource, Schema, _clock, log.For<JobChangeFeed>())
        {
            ProbePatience = TimeSpan.FromSeconds(1)
        };
        var listening = feed.ListeningAsync(CancellationToken.None);

        await log.WaitForAsync(LogLevel.Warning, "ListenConnectionString", Patience);

        Assert.False(listening.IsCompleted);
    }

    [PostgresFact]
    public async Task Workers_that_cannot_listen_still_take_jobs_every_poll_interval()
    {
        var log = new LogLines();
        using var host = await StartAsync(log, options =>
        {
            options.ListenConnectionString = AnotherDatabase();
            options.PollInterval = TimeSpan.FromMilliseconds(200);
        });

        await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await log.WaitForAsync(LogLevel.Warning, "ListenConnectionString", Patience);
        await CompletedAsync();

        await host.StopAsync();
    }

    [PostgresFact]
    public async Task With_listening_turned_off_nothing_connects_and_workers_still_take_jobs_every_poll_interval()
    {
        var log = new LogLines();
        using var host = await StartAsync(log, options =>
        {
            options.ListenForChanges = false;
            options.ListenConnectionString = "Host=127.0.0.1;Port=1;Database=nowhere;Username=nobody;Timeout=1";
            options.PollInterval = TimeSpan.FromMilliseconds(200);
        });

        await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await CompletedAsync();
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(FeedState.Off, host.Services.GetRequiredService<JobChangeFeed>().State);
        Assert.DoesNotContain(log.Lines, line => line.Level >= LogLevel.Warning);
        await host.StopAsync();
    }

    [PostgresFact]
    public async Task A_listen_connection_string_of_its_own_carries_the_changes_that_wake_an_idle_worker()
    {
        var name = $"listen-{Schema}"[..30];
        using var host = await StartAsync(new LogLines(), options =>
        {
            options.ListenConnectionString = new NpgsqlConnectionStringBuilder(ListenConnectionString!) { ApplicationName = name }.ConnectionString;
            options.PollInterval = TimeSpan.FromHours(1);
        });
        await host.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(CancellationToken.None);

        var started = DateTime.UtcNow;
        await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await CompletedAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM pg_stat_activity WHERE application_name = '{name}' AND query = 'LISTEN {Storage.ChangeChannel.For(Schema)}'"));
        await host.StopAsync();
    }

    private async Task<IHost> StartAsync(LogLines log, Action<SyncLaneOptions> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.ListenConnectionString = ListenConnectionString;
            options.Schema = Schema;
            options.RunPlanner = false;
            options.AddQueue(Queue, queue => queue.Handle<ProductHandler>("orders"));
            configure(options);
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private async Task CompletedAsync()
    {
        using var timeout = new CancellationTokenSource(Patience);
        // Sequential: polling the table until the job is done.
        while ((await JobsAsync(JobStatus.Completed)).Count == 0)
            await Task.Delay(50, timeout.Token);
    }

    /// <summary>The listening server's other database, which hears nothing written to the test database.</summary>
    private static string AnotherDatabase()
    {
        var builder = new NpgsqlConnectionStringBuilder(ListenConnectionString!);
        builder.Database = builder.Database == "postgres" ? "template1" : "postgres";
        return builder.ConnectionString;
    }

    private JobChangeFeed Feed() => new(ListenConnectionString!, Store.DataSource, Schema, _clock, NullLogger<JobChangeFeed>.Instance);

    private static async Task<List<JobChange>> CollectAsync(IAsyncEnumerable<JobChange> changes, int count)
    {
        using var timeout = new CancellationTokenSource(Patience);
        var seen = new List<JobChange>();

        // Sequential: reading changes in the order they were announced.
        await foreach (var change in changes.WithCancellation(timeout.Token))
        {
            seen.Add(change);
            if (seen.Count == count)
                break;
        }

        return seen;
    }
}
