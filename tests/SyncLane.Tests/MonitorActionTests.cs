using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>The repairs an operator makes from what the monitor shows. None of them touches a running job.</summary>
public class MonitorActionTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task One_given_up_job_is_requeued_with_its_cursor_and_a_running_or_missing_one_is_refused()
    {
        var abandoned = await InsertRowAsync("a", JobMode.Recurring, JobStatus.Abandoned, Noon.AddHours(-1), key: "1", completedAt: Noon);
        var running = await InsertRowAsync("a", JobMode.Recurring, JobStatus.Running, Noon.AddHours(-1), key: "2");
        await ExecuteAsync($"UPDATE {Schema}.jobs SET cursor = 'page-7', attempt_count = 6, error = 'SL5106: refused' WHERE id = {abandoned}");
        var monitor = Monitor();

        var requeued = await monitor.RequeueJobAsync(abandoned);
        var busy = await monitor.RequeueJobAsync(running);
        var missing = await monitor.RequeueJobAsync(999_999);

        var job = await JobAsync(abandoned);
        Assert.Equal(1, requeued.GetValueOrThrow());
        Assert.Equal((JobStatus.Pending, "page-7", 0, null, null), (job.Status, job.Cursor, job.AttemptCount, job.Error, job.CompletedAt));
        Assert.Equal((SyncErrorCodes.JobStatusNotAllowed, SyncErrorKind.Conflict), (busy.Error!.Code, busy.Error.Kind));
        Assert.Contains("is running", busy.Error.Message);
        Assert.Equal(JobStatus.Running, (await JobAsync(running)).Status);
        Assert.Equal((SyncErrorCodes.JobNotFound, SyncErrorKind.NotFound), (missing.Error!.Code, missing.Error.Kind));
    }

    [PostgresFact]
    public async Task A_restarted_job_runs_its_whole_window_again_even_after_it_completed()
    {
        var completed = await InsertRowAsync("a", JobMode.Recurring, JobStatus.Completed, Noon.AddHours(-1), Noon.AddHours(-2), Noon.AddHours(-1), "1", Noon);
        var running = await InsertRowAsync("a", JobMode.Recurring, JobStatus.Running, Noon.AddHours(-1), key: "2");
        await ExecuteAsync($"UPDATE {Schema}.jobs SET cursor = 'page-3', processed_count = 300 WHERE id = {completed}");
        var monitor = Monitor();

        var restarted = await monitor.RestartJobAsync(completed);
        var busy = await monitor.RestartJobAsync(running);

        var job = await JobAsync(completed);
        Assert.Equal(1, restarted.GetValueOrThrow());
        Assert.Equal((JobStatus.Pending, null, 0L, null), (job.Status, job.Cursor, job.ProcessedCount, job.CompletedAt));
        Assert.Equal((Noon.AddHours(-2), Noon.AddHours(-1)), (job.WindowStart, job.WindowEnd));
        Assert.Equal(SyncErrorCodes.JobStatusNotAllowed, busy.Error!.Code);
    }

    [PostgresFact]
    public async Task Requeueing_by_code_takes_every_job_of_that_code_on_the_queue_and_nothing_else()
    {
        await InsertFailedAsync("a", JobStatus.Abandoned, "JobAbandonedException: SL5106: The API answered 401.");
        await InsertFailedAsync("b", JobStatus.Deferred, "JobAbandonedException: SL5106: The API answered 403.");
        await InsertFailedAsync("c", JobStatus.Failed, "SL5106: refused");
        await InsertFailedAsync("d", JobStatus.Failed, "HttpRequestException: SL5109: unreachable");
        await InsertFailedAsync("e", JobStatus.Running, "JobAbandonedException: SL5106: earlier attempt");
        await InsertFailedAsync("f", JobStatus.Abandoned, "JobAbandonedException: SL5106: other application", queue: "billing");
        await InsertFailedAsync("g", JobStatus.Failed, null);

        var requeued = await Monitor().RequeueByCodeAsync(Queue, "SL5106");
        var unknown = await Monitor().RequeueByCodeAsync(Queue, "unknown");

        var jobs = await JobsAsync();
        Assert.Equal((3, 1), (requeued.GetValueOrThrow(), unknown.GetValueOrThrow()));
        Assert.Equal(["a", "b", "c", "g"], jobs.Where(job => job.Status == JobStatus.Pending).Select(job => job.Owner));
        Assert.Equal(JobStatus.Running, Assert.Single(jobs, job => job.Owner == "e").Status);
        Assert.Equal(1, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.jobs WHERE queue = 'billing' AND status = 'abandoned'"));
    }

    [PostgresFact]
    public async Task A_prioritized_owner_s_jobs_run_before_everyone_else_s_in_their_own_type_order_once()
    {
        // A lane of its own per job, so the lease order shows priority alone.
        await EnqueueAsync(Noon.AddHours(-1),
            Planned("other", type: "orders", lane: "o1", priority: 0, key: "o1"),
            Planned("vip", type: "products", lane: "v2", priority: 1, key: "v2"),
            Planned("vip", type: "orders", lane: "v1", priority: 0, key: "v1"),
            Planned("other", type: "products", lane: "o2", priority: 1, key: "o2"));
        var monitor = Monitor();

        var moved = await monitor.PrioritizeOwnerAsync(Queue, "vip");
        var again = await monitor.PrioritizeOwnerAsync(Queue, "vip");

        var order = new List<string>();
        for (var slot = 0; slot < 4; slot++)
            order.Add((await LeaseAsync($"w{slot}", Noon))!.DedupKey!);

        Assert.Equal((2, 0), (moved.GetValueOrThrow(), again.GetValueOrThrow()));
        Assert.Equal(["v1", "v2", "o1", "o2"], order);
    }

    private Monitoring.SyncLaneMonitor Monitor() => MonitorTests.MonitorFor(Store, Schema, _clock);

    private Task<int> InsertFailedAsync(string owner, JobStatus status, string? error, string queue = Queue) => ExecuteAsync(
        $"INSERT INTO {Schema}.jobs (queue, owner, lane, type, mode, status, error, dedup_key, next_run_at, created_at, updated_at) VALUES " +
        $"('{queue}', '{owner}', '{owner}-{Guid.NewGuid():N}', 'orders', 'recurring', '{Storage.JobText.Of(status)}', " +
        $"{(error is null ? "NULL" : $"'{error}'")}, '{Guid.NewGuid():N}', '{Noon:O}', '{Noon:O}', '{Noon:O}')");
}
