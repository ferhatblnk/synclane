using SyncLane.Planning;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>The planner reads a summary instead of every job ever created, so old jobs can go without changing what it plans.</summary>
public class CoverageTests : PostgresTest
{
    private static readonly DateTimeOffset Today = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly JobOwner Owner = new("store-1");
    private static readonly TimeSpan Slice = TimeSpan.FromDays(30);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private readonly ManualClock _clock = new(Today.AddHours(9));

    [PostgresFact]
    public async Task After_old_recurring_jobs_are_deleted_the_next_window_still_continues_where_the_last_one_ended()
    {
        var queue = Orders();
        await PlanAtAsync(queue, 9, 0);
        await CompleteEverythingAsync();
        await PlanAtAsync(queue, 9, 20);
        await CompleteEverythingAsync();

        await ExecuteAsync($"DELETE FROM {Schema}.jobs WHERE mode = 'recurring'");
        var created = await PlanAtAsync(queue, 9, 40);

        var next = Assert.Single(await JobsAsync(JobStatus.Pending));
        Assert.Equal(1, created);
        Assert.Equal((Today.AddHours(9).AddMinutes(15), Today.AddHours(9).AddMinutes(40)), (next.WindowStart, next.WindowEnd));
    }

    [PostgresFact]
    public async Task A_recurring_job_that_keeps_being_given_up_is_replaced_each_period_by_one_that_reaches_back_over_it()
    {
        var queue = Orders();
        await PlanAtAsync(queue, 9, 0);
        await ExecuteAsync($"UPDATE {Schema}.jobs SET status = 'completed', completed_at = now() WHERE mode = 'initial'");
        var first = Assert.Single(await RecurringAsync());

        var counts = new List<int>();
        for (var period = 1; period <= 8; period++)
        {
            await ExecuteAsync($"UPDATE {Schema}.jobs SET status = 'abandoned', error = 'SL5106' WHERE mode = 'recurring'");
            await PlanAtAsync(queue, 9 + period / 4, period % 4 * 15 + 5);
            counts.Add((await RecurringAsync()).Count);
        }

        var latest = Assert.Single(await RecurringAsync());
        Assert.All(counts, count => Assert.Equal(1, count));
        Assert.Equal((JobStatus.Pending, first.WindowStart), (latest.Status, latest.WindowStart));
        Assert.Equal(Today.AddHours(11).AddMinutes(5), latest.WindowEnd);

        await ExecuteAsync($"UPDATE {Schema}.jobs SET status = 'completed', completed_at = now() WHERE mode = 'recurring'");
        await PlanAtAsync(queue, 11, 20);
        var afterTheFix = Assert.Single(await JobsAsync(JobStatus.Pending));
        Assert.Equal(Today.AddHours(11), afterTheFix.WindowStart);
    }

    [PostgresFact]
    public async Task A_snapshot_refresh_that_was_given_up_is_replaced_by_the_next_one()
    {
        var queue = Options(options => options.Snapshot<ProductHandler>("products", every: TimeSpan.FromHours(1)));
        await PlanAtAsync(queue, 9, 0);
        await CompleteEverythingAsync();
        await PlanAtAsync(queue, 10, 0);
        await ExecuteAsync($"UPDATE {Schema}.jobs SET status = 'abandoned' WHERE mode = 'recurring'");

        var created = await PlanAtAsync(queue, 11, 0);

        Assert.Equal(1, created);
        Assert.Equal(JobStatus.Pending, Assert.Single(await RecurringAsync()).Status);
    }

    [PostgresFact]
    public async Task A_given_up_job_that_was_requeued_before_the_planner_wrote_is_kept()
    {
        var abandoned = await InsertRowAsync("store-1", JobMode.Recurring, JobStatus.Abandoned, Today, Today, Today.AddHours(1), "recurring:a");
        await Store.RequeueAsync(Queue, "store-1", Today, CancellationToken.None);

        var replacement = Planned("store-1", mode: JobMode.Recurring, key: "recurring:b", windowStart: Today, windowEnd: Today.AddHours(2))
            with { Replaces = [abandoned] };
        await EnqueueAsync(Today.AddHours(2), replacement);

        Assert.Equal([JobStatus.Pending, JobStatus.Pending], (await JobsAsync()).Select(job => job.Status));
    }

    [PostgresFact]
    public async Task Jobs_created_before_the_summary_existed_are_counted_in_it_so_history_is_not_loaded_again()
    {
        await ExecuteAsync($"DROP SCHEMA {Schema} CASCADE");
        await ExecuteAsync(Migrations.HistoryTable(Schema));
        await ExecuteAsync(string.Concat(Migrations.All.Take(4).Select(migration => migration.Sql(Schema) + Migrations.Record(Schema, migration))));
        await InsertRowAsync("store-1", JobMode.Initial, JobStatus.Completed, Today, Today.AddDays(-60), Today.AddDays(-30), DedupKeys.InitialWindow(Today.AddDays(-60)));
        await InsertRowAsync("store-1", JobMode.Initial, JobStatus.Completed, Today, Today.AddDays(-30), Today, DedupKeys.InitialWindow(Today.AddDays(-30)));
        await InsertRowAsync("store-1", JobMode.Recurring, JobStatus.Completed, Today.AddHours(9), Today.AddHours(8).AddMinutes(40), Today.AddHours(9), "recurring:9");

        await Store.MigrateAsync(CancellationToken.None);
        var created = await PlanAtAsync(Orders(), 9, 20);

        var next = Assert.Single(await JobsAsync(JobStatus.Pending));
        Assert.Equal(1, created);
        Assert.Equal(Today.AddHours(8).AddMinutes(55), next.WindowStart);
    }

    [PostgresFact]
    public async Task A_job_an_older_instance_created_reaches_the_summary_the_next_time_it_is_proposed()
    {
        await InsertRowAsync("store-1", JobMode.Initial, JobStatus.Pending, Today, Today.AddDays(-60), Today.AddDays(-30), DedupKeys.InitialWindow(Today.AddDays(-60)));
        await InsertRowAsync("store-1", JobMode.Initial, JobStatus.Pending, Today, Today.AddDays(-30), Today, DedupKeys.InitialWindow(Today.AddDays(-30)));

        var first = await PlanAtAsync(Orders(), 9, 0);
        var initialFrom = await ScalarAsync<DateTime>($"SELECT initial_from FROM {Schema}.coverage WHERE owner = 'store-1'");

        Assert.Equal(1, first);
        Assert.Equal(Today.AddDays(-60).UtcDateTime, initialFrom);
    }

    private async Task<List<Job>> RecurringAsync() =>
        (await JobsAsync()).Where(job => job.Mode == JobMode.Recurring).ToList();

    private Task<int> CompleteEverythingAsync() =>
        ExecuteAsync($"UPDATE {Schema}.jobs SET status = 'completed', completed_at = now() WHERE status = 'pending'");

    private Task<int> PlanAtAsync(QueueOptions queue, int hour, int minute)
    {
        _clock.Set(Today.AddHours(hour).AddMinutes(minute));
        return new QueuePlanner(Store, _clock).PlanAsync(queue, [Owner], CancellationToken.None);
    }

    private static QueueOptions Orders() =>
        Options(options => options.Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), Slice, Every, Overlap));

    private static QueueOptions Options(Action<QueueOptions> configure) =>
        new SyncLaneOptions().AddQueue(Queue, configure).Queue(Queue);
}
