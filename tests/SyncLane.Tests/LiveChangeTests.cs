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
        await using var feed = Feed(Store.DataSource);
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
        await using var otherInstance = NpgsqlDataSource.Create(ConnectionString!);
        await using var feed = Feed(otherInstance);
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
        await using var feed = Feed(Store.DataSource);
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
        await using var feed = Feed(Store.DataSource);
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

    private JobChangeFeed Feed(NpgsqlDataSource dataSource) => new(dataSource, Schema, _clock, NullLogger<JobChangeFeed>.Instance);

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
