using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class StoreTests : PostgresTest
{
    [PostgresFact]
    public async Task The_same_key_creates_one_job_however_often_it_is_enqueued()
    {
        var first = await EnqueueAsync(Noon, Planned("a", key: "initial"), Planned("a", key: "initial"));
        var second = await EnqueueAsync(Noon.AddHours(1), Planned("a", key: "initial"));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(await JobsAsync());
    }

    [PostgresFact]
    public async Task The_key_is_unique_per_owner_and_type_and_a_missing_key_is_never_a_duplicate()
    {
        var created = await EnqueueAsync(
            Noon,
            Planned("a", "orders", key: "initial"),
            Planned("b", "orders", key: "initial"),
            Planned("a", "products", key: "initial"),
            Planned("a", "orders", key: null, mode: JobMode.Manual),
            Planned("a", "orders", key: null, mode: JobMode.Manual));

        Assert.Equal(5, created);
    }

    [PostgresFact]
    public async Task A_job_is_stored_with_its_window_and_read_back_as_it_was()
    {
        var start = Noon.AddDays(-30);
        await EnqueueAsync(Noon, Planned("a", "orders", lane: "seller-7", priority: 3, key: "w", windowStart: start, windowEnd: Noon));

        var job = Assert.Single(await JobsAsync());

        Assert.Equal((Queue, "a", "seller-7", "orders"), (job.Queue, job.Owner, job.Lane, job.Type));
        Assert.Equal((JobMode.Initial, JobStatus.Pending, 3, "w"), (job.Mode, job.Status, job.Priority, job.DedupKey));
        Assert.Equal((start, Noon, Noon, Noon), (job.WindowStart, job.WindowEnd, job.NextRunAt, job.CreatedAt));
        Assert.Equal((0, 0L), (job.AttemptCount, job.ProcessedCount));
        Assert.Null(job.Cursor);
    }

    [PostgresFact]
    public async Task Requeue_gives_stopped_jobs_fresh_attempts_and_keeps_their_cursors()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("b", key: "1"), Planned("c", key: "1"), Planned("d", key: "1"));
        await StopAsync("a", JobStatus.Failed);
        await StopAsync("b", JobStatus.Deferred);
        await StopAsync("c", JobStatus.Abandoned);
        await StopAsync("d", JobStatus.Completed);

        var requeued = await Store.RequeueAsync(Queue, null, Noon.AddHours(2), CancellationToken.None);

        var jobs = await JobsAsync();
        Assert.Equal(3, requeued);
        Assert.Equal(JobStatus.Completed, jobs.Single(job => job.Owner == "d").Status);
        Assert.All(jobs.Where(job => job.Owner != "d"), job =>
        {
            Assert.Equal((JobStatus.Pending, 0, "page-7", Noon.AddHours(2)), (job.Status, job.AttemptCount, job.Cursor, job.NextRunAt));
            Assert.Null(job.Error);
            Assert.Null(job.CompletedAt);
        });
    }

    [PostgresFact]
    public async Task Requeue_can_be_limited_to_one_owner()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("b", key: "1"));
        await StopAsync("a", JobStatus.Abandoned);
        await StopAsync("b", JobStatus.Abandoned);

        Assert.Equal(1, await Store.RequeueAsync(Queue, "b", Noon, CancellationToken.None));
        Assert.Equal(JobStatus.Abandoned, (await JobsAsync()).Single(job => job.Owner == "a").Status);
    }

    [PostgresFact]
    public async Task Cancel_abandons_what_has_not_started_and_leaves_the_rest_alone()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("a", key: "2"), Planned("b", key: "1"));
        var running = await LeaseAsync("w", Noon);

        var cancelled = await Store.CancelAsync(Queue, "a", "The account was disconnected.", Noon, CancellationToken.None);

        var jobs = await JobsAsync();
        Assert.Equal(1, cancelled);
        Assert.Equal(JobStatus.Running, jobs.Single(job => job.Id == running!.Id).Status);
        Assert.Equal(JobStatus.Pending, jobs.Single(job => job.Owner == "b").Status);
        var abandoned = jobs.Single(job => job.Status == JobStatus.Abandoned);
        Assert.Equal(("a", "The account was disconnected.", Noon), (abandoned.Owner, abandoned.Error, abandoned.CompletedAt));
    }

    [PostgresFact]
    public async Task Progress_counts_the_first_load_and_nothing_else()
    {
        await EnqueueAsync(
            Noon,
            Planned("a", "products", lane: "p", key: "initial"),
            Planned("a", "orders", lane: "o1", key: "w1"),
            Planned("a", "orders", lane: "o2", key: "w2"),
            Planned("a", "orders", lane: "o3", key: "w3"),
            Planned("a", "orders", lane: "r", key: "recurring", mode: JobMode.Recurring));

        var nothingYet = await Store.GetProgressAsync(Queue, "a", CancellationToken.None);
        await StopAsync("a", JobStatus.Abandoned, lane: "o1");
        await StopAsync("a", JobStatus.Failed, lane: "o2");
        await StopAsync("a", JobStatus.Completed, lane: "p");
        var underway = await Store.GetProgressAsync(Queue, "a", CancellationToken.None);

        Assert.Equal(new OwnerProgress(4, 0, 4, 0), nothingYet);
        Assert.Equal(new OwnerProgress(4, 1, 2, 1), underway);
        Assert.True(underway.IsLoading);
        Assert.False(underway.IsComplete);
        Assert.Equal(new OwnerProgress(0, 0, 0, 0), await Store.GetProgressAsync(Queue, "nobody", CancellationToken.None));
    }

    [PostgresFact]
    public async Task Jobs_can_be_listed_by_owner_and_status_newest_first()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("a", key: "2"), Planned("b", key: "1"));
        await StopAsync("b", JobStatus.Failed);

        var (ofA, _) = await Store.GetJobsAsync(new JobQuery { Queue = Queue, Owner = "a" }, null, 100, false, CancellationToken.None);
        var (failed, _) = await Store.GetJobsAsync(new JobQuery { Status = JobStatus.Failed }, null, 100, false, CancellationToken.None);
        var (one, total) = await Store.GetJobsAsync(new JobQuery(), null, 1, true, CancellationToken.None);

        Assert.Equal(["2", "1"], ofA.Select(job => job.DedupKey));
        Assert.Equal("b", Assert.Single(failed).Owner);
        Assert.Equal("b", Assert.Single(one).Owner);
        Assert.Equal(3, total);
    }

    [PostgresFact]
    public async Task Creating_the_schema_is_safe_to_repeat_and_to_race()
    {
        await Store.MigrateAsync(CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store.MigrateAsync(CancellationToken.None)));

        Assert.Equal(1, await EnqueueAsync(Noon, Planned("a", key: "1")));
    }

    private async Task StopAsync(string owner, JobStatus status, string? lane = null)
    {
        var job = (await JobsAsync(JobStatus.Pending)).First(candidate => candidate.Owner == owner && (lane is null || candidate.Lane == lane));

        await ExecuteAsync(
            $"""
            UPDATE {Schema}.jobs
            SET status = '{status.ToString().ToLowerInvariant()}',
                cursor = 'page-7',
                attempt_count = 3,
                error = 'The other side did not answer.',
                completed_at = now()
            WHERE id = {job.Id}
            """);
    }
}
