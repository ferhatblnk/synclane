using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Fetching an owner's new data now, as a webhook asks, without disturbing what the planner does.</summary>
public class SyncNowTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_sync_now_fetches_from_where_the_owner_s_completed_data_reaches_to_now_in_its_lane_at_high_priority()
    {
        var reached = await LoadHistoryAsync(new JobOwner("a", Lane: "account-1"));
        _clock.Set(Noon.AddHours(2));

        var id = (await Monitor().SyncNowAsync(Queue, "a", "orders", by: "webhook")).GetValueOrThrow();

        var job = await JobAsync(id);
        Assert.Equal((JobMode.Manual, JobStatus.Pending, JobPriority.High), (job.Mode, job.Status, job.PriorityLevel));
        Assert.Equal(("account-1", reached, Noon.AddHours(2)), (job.Lane, job.WindowStart, job.WindowEnd));
    }

    [PostgresFact]
    public async Task A_burst_of_calls_is_one_job_stretched_to_the_last_call_until_it_starts()
    {
        await LoadHistoryAsync(new JobOwner("a"));
        var monitor = Monitor();

        _clock.Set(Noon.AddMinutes(1));
        var first = (await monitor.SyncNowAsync(Queue, "a", "orders")).GetValueOrThrow();
        _clock.Set(Noon.AddMinutes(2));
        var second = (await monitor.SyncNowAsync(Queue, "a", "orders")).GetValueOrThrow();
        await LeaseAsync("w", Noon.AddMinutes(2));
        _clock.Set(Noon.AddMinutes(3));
        var afterStart = (await monitor.SyncNowAsync(Queue, "a", "orders")).GetValueOrThrow();

        Assert.Equal(first, second);
        Assert.Equal(Noon.AddMinutes(2), (await JobAsync(first)).WindowEnd);
        Assert.NotEqual(first, afterStart);
        Assert.Equal(Noon.AddMinutes(3), (await JobAsync(afterStart)).WindowEnd);
    }

    [PostgresFact]
    public async Task Calls_at_the_same_moment_make_one_job()
    {
        await LoadHistoryAsync(new JobOwner("a"));
        // A connection each, opened before the start, so the calls really meet in the database.
        var sources = Enumerable.Range(0, 16).Select(_ => Npgsql.NpgsqlDataSource.Create(ConnectionString!)).ToList();
        var monitors = new List<Monitoring.SyncLaneMonitor>();
        foreach (var source in sources)
        {
            await (await source.OpenConnectionAsync()).DisposeAsync();
            monitors.Add(MonitorTests.MonitorFor(new Storage.JobStore(source, new Storage.JobSql(Schema)), Schema, _clock));
        }

        using var start = new ManualResetEventSlim();
        var calls = monitors.Select(monitor => Task.Run(async () =>
        {
            start.Wait();
            return await monitor.SyncNowAsync(Queue, "a", "orders");
        })).ToList();
        start.Set();
        var ids = await Task.WhenAll(calls);
        foreach (var source in sources)
            await source.DisposeAsync();

        Assert.Single(ids.Select(result => result.GetValueOrThrow()).Distinct());
        Assert.Single(await JobsAsync(), job => job.DedupKey!.StartsWith(DedupKeys.SyncNowPrefix, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task A_completed_sync_now_moves_the_owner_s_completed_data_to_its_end_and_a_manual_job_past_a_gap_does_not()
    {
        var reached = await LoadHistoryAsync(new JobOwner("a"));
        var client = Client();
        var beyondGap = (await client.EnqueueAsync(new NewJob
        {
            Queue = Queue, Owner = new JobOwner("a"), Type = "orders", WindowStart = reached.AddDays(1), WindowEnd = reached.AddDays(2)
        })).GetValueOrThrow();
        await RunAsync(beyondGap, Noon);

        var afterGap = await CompletedToAsync("a");
        _clock.Set(Noon.AddHours(1));
        var synced = (await Monitor().SyncNowAsync(Queue, "a", "orders")).GetValueOrThrow();
        await RunAsync(synced, Noon.AddHours(1));

        Assert.Equal(reached, afterGap);
        Assert.Equal(Noon.AddHours(1), await CompletedToAsync("a"));
    }

    [PostgresFact]
    public async Task A_type_without_windows_is_fetched_whole()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue.Snapshot<ProductHandler>("products"));
        await new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock).PlanAsync(Queue, new JobOwner("a"));

        var job = await JobAsync((await Monitor().SyncNowAsync(Queue, "a", "products")).GetValueOrThrow());

        Assert.Equal(((DateTimeOffset?)null, (DateTimeOffset?)null), (job.WindowStart, job.WindowEnd));
    }

    [PostgresFact]
    public async Task An_owner_or_type_the_queue_never_planned_is_not_found_and_nothing_is_made()
    {
        await LoadHistoryAsync(new JobOwner("a"));
        var before = (await JobsAsync()).Count;

        var unknownOwner = await Monitor().SyncNowAsync(Queue, "b", "orders");
        var unknownType = await Monitor().SyncNowAsync(Queue, "a", "invoices");

        Assert.Equal((SyncErrorCodes.OwnerTypeNotPlanned, SyncErrorKind.NotFound), (unknownOwner.Error!.Code, unknownOwner.Error.Kind));
        Assert.Equal(SyncErrorCodes.OwnerTypeNotPlanned, unknownType.Error!.Code);
        Assert.Equal(before, (await JobsAsync()).Count);
    }

    [PostgresFact]
    public async Task Each_sync_now_is_in_the_actions_log_with_who_asked()
    {
        await LoadHistoryAsync(new JobOwner("a"));
        var monitor = Monitor();

        await monitor.SyncNowAsync(Queue, "a", "orders", by: "order-webhook");
        var actions = (await monitor.GetActionsAsync(new ActionQuery { Queue = Queue })).Items;

        Assert.Contains(actions, action => action is { Action: "sync now", By: "order-webhook", Owner: "a" });
    }

    /// <summary>Plans a two-slice history for the owner and completes it; returns where its completed data reaches.</summary>
    private async Task<DateTimeOffset> LoadHistoryAsync(JobOwner owner)
    {
        await Client().PlanAsync(Queue, owner);

        // Sequential: each job of the lane is taken and completed in turn.
        while (await LeaseAsync("loader", Noon) is { } job)
            await Store.CompleteAsync(job.Id, "loader", Noon, CancellationToken.None);

        return (await CompletedToAsync(owner.Key))!.Value;
    }

    private async Task RunAsync(long id, DateTimeOffset at)
    {
        var job = await LeaseAsync("runner", at);
        Assert.Equal(id, job!.Id);
        await Store.CompleteAsync(id, "runner", at, CancellationToken.None);
    }

    private async Task<DateTimeOffset?> CompletedToAsync(string owner) =>
        await ScalarAsync<DateTime?>($"SELECT completed_to FROM {Schema}.coverage WHERE queue = '{Queue}' AND owner = '{owner}' AND type = 'orders'")
            is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null;

    private SyncLaneClient Client()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue.Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30)));
        return new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock);
    }

    private Monitoring.SyncLaneMonitor Monitor() => MonitorTests.MonitorFor(Store, Schema, _clock);
}
