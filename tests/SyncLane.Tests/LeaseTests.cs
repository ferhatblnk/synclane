using System.Collections.Concurrent;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class LeaseTests : PostgresTest
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    [PostgresFact]
    public async Task Types_run_in_their_declared_order_and_the_newest_window_first()
    {
        await EnqueueAsync(
            Noon,
            Planned("a", "orders", priority: 1, key: "older", windowStart: Noon.AddDays(-60), windowEnd: Noon.AddDays(-30)),
            Planned("a", "orders", priority: 1, key: "newer", windowStart: Noon.AddDays(-30), windowEnd: Noon),
            Planned("a", "products", priority: 0, key: "initial"));

        var order = new List<string>();
        while (await LeaseAsync("w", Noon) is { } job)
        {
            order.Add($"{job.Type}:{job.DedupKey}");
            await Store.CompleteAsync(job.Id, "w", Noon, CancellationToken.None);
        }

        Assert.Equal(["products:initial", "orders:newer", "orders:older"], order);
    }

    [PostgresFact]
    public async Task What_is_new_runs_before_history_that_was_planned_earlier()
    {
        await EnqueueAsync(
            Noon,
            Planned("a", key: "history", windowStart: Noon.AddDays(-30), windowEnd: Noon.AddDays(-1)));
        await EnqueueAsync(
            Noon.AddHours(2),
            Planned("a", key: "fresh", mode: JobMode.Recurring, windowStart: Noon.AddDays(-1), windowEnd: Noon.AddHours(2)));

        var first = await LeaseAsync("w", Noon.AddHours(2));

        Assert.Equal("fresh", first!.DedupKey);
    }

    [PostgresFact]
    public async Task Taking_a_job_marks_it_as_held()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));

        var job = await LeaseAsync("host:1/stores#1", Noon.AddSeconds(3), FiveMinutes);

        Assert.NotNull(job);
        Assert.Equal((JobStatus.Running, 1, "host:1/stores#1"), (job.Status, job.AttemptCount, job.LeasedBy));
        Assert.Equal((Noon.AddSeconds(3), Noon.AddSeconds(3), Noon.AddSeconds(3) + FiveMinutes), (job.StartedAt, job.HeartbeatAt, job.LeaseExpiresAt));
    }

    [PostgresFact]
    public async Task Jobs_of_one_lane_run_one_at_a_time_while_lanes_run_side_by_side()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("a", key: "2"), Planned("b", key: "1"));

        var first = await LeaseAsync("w1", Noon);
        var second = await LeaseAsync("w2", Noon);
        var third = await LeaseAsync("w3", Noon);
        await Store.CompleteAsync(first!.Id, "w1", Noon, CancellationToken.None);
        var afterwards = await LeaseAsync("w3", Noon);

        Assert.Equal(("a", "1"), (first.Owner, first.DedupKey));
        Assert.Equal("b", second!.Owner);
        Assert.Null(third);
        Assert.Equal(("a", "2"), (afterwards!.Owner, afterwards.DedupKey));
    }

    [PostgresFact]
    public async Task Owners_that_share_a_lane_wait_for_each_other()
    {
        await EnqueueAsync(Noon, Planned("store-eu", lane: "seller-7", key: "1"), Planned("store-us", lane: "seller-7", key: "1"));

        var first = await LeaseAsync("w1", Noon);
        var second = await LeaseAsync("w2", Noon);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [PostgresFact]
    public async Task Queues_do_not_see_each_others_jobs_or_lanes()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        await Store.EnqueueAsync("warehouses", [Planned("a", key: "1")], Noon, CancellationToken.None);

        var stores = await LeaseAsync("w1", Noon);
        var warehouses = await Store.LeaseAsync("warehouses", "w2", Noon, Noon + FiveMinutes, CancellationToken.None);

        Assert.Equal(Queue, stores!.Queue);
        Assert.Equal("warehouses", warehouses!.Queue);
    }

    [PostgresFact]
    public async Task A_job_is_not_taken_before_it_is_due()
    {
        await EnqueueAsync(Noon.AddMinutes(10), Planned("a", key: "1"));

        Assert.Null(await LeaseAsync("w", Noon));
        Assert.NotNull(await LeaseAsync("w", Noon.AddMinutes(10)));
    }

    [PostgresFact]
    public async Task A_failed_job_waits_for_its_delay_and_then_continues_from_its_cursor()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var job = await LeaseAsync("w", Noon);
        await Store.CheckpointAsync(job!.Id, "w", "page-2", 100, Noon, Noon + FiveMinutes, CancellationToken.None);
        await Store.SettleAsync(job.Id, "w", JobStatus.Failed, "TimeoutException: slow", Noon.AddMinutes(1), Noon, CancellationToken.None);

        var tooEarly = await LeaseAsync("w", Noon.AddSeconds(59));
        var retried = await LeaseAsync("w", Noon.AddMinutes(1));

        Assert.Null(tooEarly);
        Assert.Equal((2, "page-2", 100L, "TimeoutException: slow"), (retried!.AttemptCount, retried.Cursor, retried.ProcessedCount, retried.Error));
    }

    [PostgresFact]
    public async Task A_job_whose_worker_went_silent_is_taken_over_and_the_old_worker_can_no_longer_write()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var original = await LeaseAsync("w1", Noon, FiveMinutes);

        var whileAlive = await LeaseAsync("w2", Noon.AddMinutes(4), FiveMinutes);
        var takenOver = await LeaseAsync("w2", Noon.AddMinutes(6), FiveMinutes);

        Assert.Null(whileAlive);
        Assert.Equal((original!.Id, 2, "w2"), (takenOver!.Id, takenOver.AttemptCount, takenOver.LeasedBy));
        Assert.Equal(Noon, takenOver.StartedAt);

        var later = Noon.AddMinutes(7);
        Assert.False(await Store.CheckpointAsync(original.Id, "w1", "stale", 50, later, later + FiveMinutes, CancellationToken.None));
        Assert.False(await Store.HeartbeatAsync(original.Id, "w1", later, later + FiveMinutes, CancellationToken.None));
        Assert.False(await Store.CompleteAsync(original.Id, "w1", later, CancellationToken.None));
        Assert.False(await Store.SettleAsync(original.Id, "w1", JobStatus.Failed, "late", later, later, CancellationToken.None));
        Assert.False(await Store.ReleaseAsync(original.Id, "w1", later, CancellationToken.None));

        Assert.True(await Store.CheckpointAsync(original.Id, "w2", "page-2", 50, later, later + FiveMinutes, CancellationToken.None));
        var current = await JobAsync(original.Id);
        Assert.Equal(("page-2", 50L, JobStatus.Running), (current.Cursor, current.ProcessedCount, current.Status));
    }

    [PostgresFact]
    public async Task A_lane_stays_closed_until_the_job_of_its_silent_worker_is_taken_over()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("a", key: "2"));
        var stuck = await LeaseAsync("w1", Noon, FiveMinutes);

        var next = await LeaseAsync("w2", Noon.AddMinutes(6), FiveMinutes);

        Assert.Equal(stuck!.Id, next!.Id);
    }

    [PostgresFact]
    public async Task A_heartbeat_keeps_the_job_with_its_worker()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var job = await LeaseAsync("w1", Noon, FiveMinutes);

        var renewed = await Store.HeartbeatAsync(job!.Id, "w1", Noon.AddMinutes(4), Noon.AddMinutes(9), CancellationToken.None);

        Assert.True(renewed);
        Assert.Null(await LeaseAsync("w2", Noon.AddMinutes(6), FiveMinutes));
        Assert.NotNull(await LeaseAsync("w2", Noon.AddMinutes(9), FiveMinutes));
    }

    [PostgresFact]
    public async Task A_deferred_job_runs_last_and_only_once_its_lane_is_idle()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("a", key: "2"), Planned("b", key: "1"));
        var troubled = await LeaseAsync("w", Noon);
        await Store.SettleAsync(troubled!.Id, "w", JobStatus.Deferred, "boom", Noon, Noon, CancellationToken.None);

        var order = new List<string>();
        while (await LeaseAsync("w", Noon) is { } job)
        {
            order.Add($"{job.Owner}:{job.DedupKey}");
            await Store.CompleteAsync(job.Id, "w", Noon, CancellationToken.None);
        }

        Assert.Equal(["a:2", "b:1", "a:1"], order);
    }

    [PostgresFact]
    public async Task A_deferred_job_keeps_waiting_while_another_job_of_its_lane_waits_for_a_retry()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("a", key: "2"));
        var first = await LeaseAsync("w", Noon);
        await Store.SettleAsync(first!.Id, "w", JobStatus.Deferred, "boom", Noon, Noon, CancellationToken.None);
        var second = await LeaseAsync("w", Noon);
        await Store.SettleAsync(second!.Id, "w", JobStatus.Failed, "boom", Noon.AddMinutes(10), Noon, CancellationToken.None);

        var meanwhile = await LeaseAsync("w", Noon.AddMinutes(5));
        var whenDue = await LeaseAsync("w", Noon.AddMinutes(10));

        Assert.Null(meanwhile);
        Assert.Equal(second.Id, whenDue!.Id);
    }

    [PostgresFact]
    public async Task A_job_handed_back_keeps_its_cursor_and_does_not_lose_an_attempt()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var job = await LeaseAsync("w1", Noon);
        await Store.CheckpointAsync(job!.Id, "w1", "page-4", 300, Noon, Noon + FiveMinutes, CancellationToken.None);

        var released = await Store.ReleaseAsync(job.Id, "w1", Noon.AddMinutes(1), CancellationToken.None);
        var waiting = await JobAsync(job.Id);
        var resumed = await LeaseAsync("w2", Noon.AddMinutes(1));

        Assert.True(released);
        Assert.Equal((JobStatus.Pending, 0, "page-4"), (waiting.Status, waiting.AttemptCount, waiting.Cursor));
        Assert.Null(waiting.LeasedBy);
        Assert.Equal((1, "page-4", 300L), (resumed!.AttemptCount, resumed.Cursor, resumed.ProcessedCount));
    }

    [PostgresFact]
    public async Task Completing_a_job_clears_what_only_mattered_while_it_ran()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var job = await LeaseAsync("w", Noon);
        await Store.CheckpointAsync(job!.Id, "w", "page-2", 10, Noon, Noon + FiveMinutes, CancellationToken.None);

        await Store.CompleteAsync(job.Id, "w", Noon.AddMinutes(2), CancellationToken.None);

        var done = await JobAsync(job.Id);
        Assert.Equal((JobStatus.Completed, 10L, Noon.AddMinutes(2)), (done.Status, done.ProcessedCount, done.CompletedAt));
        Assert.Null(done.Cursor);
        Assert.Null(done.LeasedBy);
        Assert.Null(done.LeaseExpiresAt);
    }

    [PostgresFact]
    public async Task Many_workers_never_put_two_jobs_of_one_lane_to_work()
    {
        const int lanes = 6;
        const int jobsPerLane = 15;
        const int workers = 12;
        const int total = lanes * jobsPerLane;

        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        await EnqueueAsync(start, Enumerable.Range(0, total).Select(index => Planned($"lane-{index % lanes}", key: index.ToString())).ToArray());

        var atWork = new ConcurrentDictionary<string, int>();
        var overlaps = 0;
        var completed = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        async Task WorkAsync(string worker)
        {
            while (Volatile.Read(ref completed) < total)
            {
                timeout.Token.ThrowIfCancellationRequested();

                var job = await LeaseAsync(worker, DateTimeOffset.UtcNow);
                if (job is null)
                {
                    await Task.Delay(2, timeout.Token);
                    continue;
                }

                if (atWork.AddOrUpdate(job.Lane, 1, (_, count) => count + 1) > 1)
                    Interlocked.Increment(ref overlaps);

                await Task.Delay(Random.Shared.Next(1, 4), timeout.Token);
                atWork.AddOrUpdate(job.Lane, 0, (_, count) => count - 1);

                Assert.True(await Store.CompleteAsync(job.Id, worker, DateTimeOffset.UtcNow, CancellationToken.None));
                Interlocked.Increment(ref completed);
            }
        }

        await Task.WhenAll(Enumerable.Range(1, workers).Select(index => Task.Run(() => WorkAsync($"w{index}"))));

        var jobs = await JobsAsync();
        Assert.Equal(0, overlaps);
        Assert.Equal(total, completed);
        Assert.All(jobs, job => Assert.Equal((JobStatus.Completed, 1), (job.Status, job.AttemptCount)));
    }
}
