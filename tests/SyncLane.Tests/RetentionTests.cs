using Microsoft.Extensions.Logging.Abstractions;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class RetentionTests : PostgresTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    [PostgresFact]
    public async Task Only_completed_recurring_and_manual_jobs_older_than_the_setting_are_deleted()
    {
        var month = Now.AddDays(-30);
        await InsertRowAsync("a", JobMode.Initial, JobStatus.Completed, month, key: "initial", completedAt: month);
        await InsertRowAsync("a", JobMode.Recurring, JobStatus.Completed, month, key: "old", completedAt: month);
        await InsertRowAsync("a", JobMode.Manual, JobStatus.Completed, month, key: "manual", completedAt: month);
        await InsertRowAsync("a", JobMode.Recurring, JobStatus.Completed, Now.AddDays(-1), key: "recent", completedAt: Now.AddDays(-1));
        await InsertRowAsync("a", JobMode.Recurring, JobStatus.Abandoned, month, key: "given-up", completedAt: month);
        await InsertRowAsync("a", JobMode.Recurring, JobStatus.Pending, month, key: "waiting");
        await InsertRowAsync("a", JobMode.Recurring, JobStatus.Completed, month, key: "other-app", completedAt: month, queue: "another-application");

        var deleted = await Retention().CleanAsync([Queue], Week, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(["initial", "recent", "given-up", "waiting"], (await JobsAsync()).Select(job => job.DedupKey));
        Assert.Equal(1, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.jobs WHERE queue = 'another-application'"));
    }

    [PostgresFact]
    public async Task Cleaning_goes_on_batch_after_batch_until_nothing_due_is_left()
    {
        var count = RetentionService.BatchSize * 2 + 345;
        await ExecuteAsync(
            $"""
            INSERT INTO {Schema}.jobs (queue, owner, lane, type, mode, status, dedup_key, next_run_at, created_at, completed_at, updated_at)
            SELECT '{Queue}', 'a', 'a', 'orders', 'recurring', 'completed', 'r' || n, now(), now(), '{Now.AddDays(-30):O}'::timestamptz, now()
            FROM generate_series(1, {count}) AS n
            """);

        var deleted = await Retention().CleanAsync([Queue], Week, CancellationToken.None);

        Assert.Equal(count, deleted);
        Assert.Equal(0, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.jobs"));
    }

    private RetentionService Retention() =>
        new(new SyncLaneOptions(), Store, new ManualClock(Now), NullLogger<RetentionService>.Instance);
}
