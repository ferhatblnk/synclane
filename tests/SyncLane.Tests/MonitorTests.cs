using SyncLane.Monitoring;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>The questions an operator asks, answered from the summary tables and the open jobs.</summary>
public class MonitorTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task The_overview_counts_each_queue_s_open_jobs_its_owners_instances_and_last_day()
    {
        await EnqueueAsync(Noon.AddHours(-2), Planned("a", key: "1"), Planned("b", key: "2"));
        var done = (await LeaseAsync("w", Noon.AddHours(-1)))!;
        await Store.CheckpointAsync(done.Id, "w", "page-2", 7, Noon.AddHours(-1), Noon, CancellationToken.None);
        await Store.CompleteAsync(done.Id, "w", Noon.AddHours(-1), CancellationToken.None);
        var failing = (await LeaseAsync("w", Noon.AddHours(-1)))!;
        await Store.SettleAsync(failing.Id, "w", JobStatus.Failed, "TimeoutException: slow", Noon.AddHours(1), Noon.AddHours(-1), CancellationToken.None);

        await InsertRowAsync("c", JobMode.Recurring, JobStatus.Pending, Noon.AddMinutes(-1), key: "due");
        await InsertRowAsync("c", JobMode.Recurring, JobStatus.Pending, Noon.AddMinutes(10), key: "postponed");
        await InsertRowAsync("d", JobMode.Recurring, JobStatus.Running, Noon, key: "running");
        await InsertRowAsync("d", JobMode.Recurring, JobStatus.Deferred, Noon, key: "deferred");
        await InsertRowAsync("d", JobMode.Recurring, JobStatus.Abandoned, Noon, key: "abandoned", completedAt: Noon);
        await InsertRowAsync("e", JobMode.Recurring, JobStatus.Abandoned, Noon, key: "abandoned", completedAt: Noon, queue: "billing");
        await InsertInstanceAsync("host-a:1", Noon.AddSeconds(-10), Queue);
        await InsertInstanceAsync("host-b:1", Noon.AddMinutes(-10), Queue);

        var overview = (await Monitor().GetOverviewAsync()).GetValueOrThrow();

        var billing = Assert.Single(overview, queue => queue.Queue == "billing");
        var stores = Assert.Single(overview, queue => queue.Queue == Queue);
        Assert.Equal(new QueueOverview { Queue = "billing", Abandoned = 1 }, billing);
        Assert.Equal(
            new QueueOverview
            {
                Queue = Queue, Owners = 2, Instances = 1,
                Due = 1, Postponed = 1, Running = 1, Retrying = 1, Deferred = 1, Abandoned = 1,
                CompletedLastDay = 1, RecordsLastDay = 7, FailuresLastDay = 1
            },
            stores);
    }

    [PostgresFact]
    public async Task Owners_are_ranked_by_how_old_their_data_is_with_the_job_that_explains_it()
    {
        await InsertCoverageAsync("never", "orders", completedTo: null, completedAt: null);
        await InsertCoverageAsync("broken", "orders", Noon.AddDays(-3), Noon.AddDays(-3));
        await InsertCoverageAsync("current", "orders", Noon.AddMinutes(-15), Noon.AddMinutes(-14));
        await InsertCoverageAsync("current", "products", completedTo: null, completedAt: Noon.AddHours(-1));
        await InsertJobAsync("broken", JobStatus.Failed, "TimeoutException: slow");
        await InsertJobAsync("broken", JobStatus.Abandoned, "JobAbandonedException: SL5106: The API answered 401.");
        await InsertRowAsync("never", JobMode.Initial, JobStatus.Completed, Noon, key: "s1", completedAt: Noon);
        await InsertRowAsync("never", JobMode.Initial, JobStatus.Pending, Noon, key: "s2");

        var page = (await Monitor().GetFreshnessAsync(new FreshnessQuery { Queue = Queue })).Items;

        Assert.Equal(["never", "broken", "current", "current"], page.Select(row => row.Owner));
        Assert.Equal((2, 1, JobStatus.Pending), (page[0].HistoryTotal, page[0].HistoryCompleted, page[0].Status));
        Assert.Equal((JobStatus.Abandoned, "SL5106"), (page[1].Status, page[1].Code));
        Assert.Equal(Noon.AddDays(-3), page[1].DataUntil);
        Assert.Equal(("products", Noon.AddHours(-1)), (page[2].Type, page[2].DataUntil));
        Assert.Null(page[3].Status);
    }

    [PostgresFact]
    public async Task Freshness_pages_follow_the_cursor_and_bad_input_is_refused()
    {
        for (var index = 0; index < 5; index++)
            await InsertCoverageAsync($"owner-{index}", "orders", Noon.AddHours(-index), Noon.AddHours(-index));
        var monitor = Monitor();

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await monitor.GetFreshnessAsync(new FreshnessQuery { Queue = Queue, Limit = 2, Cursor = cursor });
            seen.AddRange(page.Items.Select(row => row.Owner));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(["owner-4", "owner-3", "owner-2", "owner-1", "owner-0"], seen);
        Assert.Equal(SyncErrorCodes.CursorInvalid, (await monitor.GetFreshnessAsync(new FreshnessQuery { Cursor = "not-a-cursor" })).Error!.Code);
        Assert.Equal(SyncErrorCodes.LimitOutOfRange, (await monitor.GetFreshnessAsync(new FreshnessQuery { Limit = 0 })).Error!.Code);
    }

    [PostgresFact]
    public async Task Problems_are_grouped_by_code_most_jobs_first()
    {
        await InsertJobAsync("a", JobStatus.Abandoned, "JobAbandonedException: SL5106: The API answered 401.");
        await InsertJobAsync("a", JobStatus.Failed, "HttpRequestException: SL5109: unreachable -> SocketException: refused");
        await InsertJobAsync("b", JobStatus.Deferred, "JobAbandonedException: SL5106: The API answered 403.");
        await InsertJobAsync("c", JobStatus.Failed, "InvalidOperationException: my handler broke");
        await InsertJobAsync("d", JobStatus.Abandoned, "JobAbandonedException: SL5106: The API answered 401.");
        await InsertJobAsync("e", JobStatus.Pending, "JobPostponedException: SL5105: waiting for the budget");
        await InsertJobAsync("f", JobStatus.Abandoned, "JobAbandonedException: SL5106: other app", queue: "billing");

        var problems = (await Monitor().GetProblemsAsync(Queue)).GetValueOrThrow();

        Assert.Equal(["SL5106", "InvalidOperationException", "SL5109"], problems.Select(group => group.Code));
        Assert.Equal((3, 2, 3), (problems[0].Jobs, problems[0].Abandoned, problems[0].Owners));
        Assert.All(problems, group => Assert.Equal(Queue, group.Queue));
        Assert.Equal(2, (await Monitor().GetProblemsAsync()).GetValueOrThrow().Count(group => group.Code == "SL5106"));
    }

    [PostgresFact]
    public async Task Budgets_page_by_name_and_key_and_show_a_pause()
    {
        await InsertBucketAsync("api", "a", refilledAt: Noon.AddSeconds(30), rate: 4.5);
        await InsertBucketAsync("api", "b", refilledAt: Noon.AddSeconds(-1), rate: null);
        await InsertBucketAsync("search", "a", refilledAt: Noon, rate: 1);

        var first = await Monitor().GetBudgetsAsync(new BudgetQuery { Limit = 2 });
        var second = await Monitor().GetBudgetsAsync(new BudgetQuery { Limit = 2, Cursor = first.NextCursor });

        Assert.Equal([("api", "a"), ("api", "b")], first.Items.Select(state => (state.Budget, state.Key)));
        Assert.Equal((Noon.AddSeconds(30), 4.5), (first.Items[0].PausedUntil, first.Items[0].Rate));
        Assert.Equal(((DateTimeOffset?)null, (double?)null), (first.Items[1].PausedUntil, first.Items[1].Rate));
        Assert.Equal([("search", "a")], second.Items.Select(state => (state.Budget, state.Key)));
        Assert.Null(second.NextCursor);
    }

    [PostgresFact]
    public async Task Instances_are_listed_with_whether_they_are_alive()
    {
        await InsertInstanceAsync("host-a:1", Noon.AddSeconds(-10), Queue);
        await InsertInstanceAsync("host-b:1", Noon.AddMinutes(-10), Queue);

        var instances = (await Monitor().GetInstancesAsync()).GetValueOrThrow();

        Assert.Equal([("host-a:1", true), ("host-b:1", false)], instances.Select(instance => (instance.Id, instance.IsAlive)));
        Assert.Equal([Queue], instances[0].Queues);
    }

    [PostgresFact]
    public async Task Volume_counts_completions_records_failures_and_give_ups_per_hour_and_per_day()
    {
        await RunAsync("a", Noon.AddHours(-3), records: 10, JobStatus.Completed);
        await RunAsync("b", Noon.AddHours(-3).AddMinutes(20), records: 5, JobStatus.Completed);
        await RunAsync("c", Noon.AddHours(-1), records: 0, JobStatus.Abandoned);
        await RunAsync("d", Noon.AddHours(-1), records: 2, JobStatus.Completed);
        var monitor = Monitor();

        var hours = (await monitor.GetVolumeAsync(new VolumeQuery { From = Noon.AddHours(-6) })).GetValueOrThrow();
        var days = (await monitor.GetVolumeAsync(new VolumeQuery { From = Noon.AddDays(-1), Unit = VolumeUnit.Day })).GetValueOrThrow();
        var nothing = (await monitor.GetVolumeAsync(new VolumeQuery { From = Noon, To = Noon })).GetValueOrThrow();

        Assert.Equal(
            [
                new VolumeBucket { Start = Noon.AddHours(-3), Completed = 2, Records = 15 },
                new VolumeBucket { Start = Noon.AddHours(-1), Completed = 1, Records = 2, Failures = 1, Abandoned = 1 }
            ],
            hours);
        Assert.Equal([new VolumeBucket { Start = new DateTimeOffset(Noon.UtcDateTime.Date, TimeSpan.Zero), Completed = 3, Records = 17, Failures = 1, Abandoned = 1 }], days);
        Assert.Empty(nothing);
    }

    [PostgresFact]
    public async Task Completing_a_job_and_marking_history_record_how_far_the_data_reaches()
    {
        await EnqueueAsync(Noon, Planned("a", windowStart: Noon.AddDays(-30), windowEnd: Noon.AddDays(-1), key: "w"));
        var job = (await LeaseAsync("w", Noon))!;
        await Store.CompleteAsync(job.Id, "w", Noon, CancellationToken.None);
        await Store.MarkLoadedAsync(Queue, [Planned("b", windowStart: Noon.AddDays(-60), windowEnd: Noon.AddHours(-1), key: "loaded")], Noon, CancellationToken.None);

        var rows = (await Monitor().GetFreshnessAsync(new FreshnessQuery { Queue = Queue })).Items;

        Assert.Equal([("a", Noon.AddDays(-1)), ("b", Noon.AddHours(-1))], rows.Select(row => (row.Owner, row.DataUntil!.Value)));
    }

    [PostgresFact]
    public async Task Jobs_finished_before_the_monitoring_migration_are_counted_by_it()
    {
        await ExecuteAsync($"DROP SCHEMA {Schema} CASCADE");
        await ExecuteAsync(Migrations.HistoryTable(Schema));
        await ExecuteAsync(string.Concat(Migrations.All.Take(6).Select(migration => migration.Sql(Schema) + Migrations.Record(Schema, migration))));
        await InsertRowAsync("a", JobMode.Initial, JobStatus.Completed, Noon.AddHours(-3), Noon.AddDays(-2), Noon.AddDays(-1), "w", Noon.AddHours(-2));
        await ExecuteAsync($"UPDATE {Schema}.jobs SET processed_count = 9");
        await ExecuteAsync($"INSERT INTO {Schema}.coverage (queue, owner, type, has_initial) VALUES ('{Queue}', 'a', 'orders', true)");

        await Store.MigrateAsync(CancellationToken.None);
        var monitor = Monitor();

        var row = Assert.Single((await monitor.GetFreshnessAsync(new FreshnessQuery())).Items);
        var hour = Assert.Single((await monitor.GetVolumeAsync(new VolumeQuery { From = Noon.AddDays(-1) })).GetValueOrThrow());
        Assert.Equal(Noon.AddDays(-1), row.DataUntil);
        Assert.Equal((1, 9), (hour.Completed, hour.Records));
    }

    private SyncLaneMonitor Monitor() => new(
        new MonitorStore(Store.DataSource, new MonitorSql(Schema)),
        new BudgetStore(Store.DataSource, new BudgetSql(Schema)),
        new InstanceStore(Store.DataSource, new InstanceSql(Schema)),
        _clock);

    private async Task RunAsync(string owner, DateTimeOffset at, int records, JobStatus outcome)
    {
        await EnqueueAsync(at, Planned(owner, key: owner));
        var job = (await LeaseAsync("w", at))!;
        if (records > 0)
            await Store.CheckpointAsync(job.Id, "w", "next", records, at, at.AddMinutes(5), CancellationToken.None);

        if (outcome == JobStatus.Completed)
            await Store.CompleteAsync(job.Id, "w", at, CancellationToken.None);
        else
            await Store.SettleAsync(job.Id, "w", outcome, "JobAbandonedException: SL5106: refused", at, at, CancellationToken.None);
    }

    private Task<int> InsertCoverageAsync(string owner, string type, DateTimeOffset? completedTo, DateTimeOffset? completedAt) => ExecuteAsync(
        $"INSERT INTO {Schema}.coverage (queue, owner, type, has_initial, completed_to, completed_at) VALUES " +
        $"('{Queue}', '{owner}', '{type}', true, {Literal(completedTo)}, {Literal(completedAt)})");

    private Task<int> InsertJobAsync(string owner, JobStatus status, string error, string queue = Queue) => ExecuteAsync(
        $"INSERT INTO {Schema}.jobs (queue, owner, lane, type, mode, status, error, dedup_key, next_run_at, created_at, updated_at) VALUES " +
        $"('{queue}', '{owner}', '{owner}', 'orders', 'recurring', '{JobText.Of(status)}', '{error}', '{Guid.NewGuid():N}', " +
        $"{Literal(Noon)}, {Literal(Noon)}, {Literal(Noon)})");

    private Task<int> InsertBucketAsync(string budget, string key, DateTimeOffset refilledAt, double? rate) => ExecuteAsync(
        $"INSERT INTO {Schema}.rate_buckets (budget, key, tokens, refilled_at, updated_at, rate) VALUES " +
        $"('{budget}', '{key}', 1, {Literal(refilledAt)}, {Literal(Noon)}, {(rate is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")})");

    private Task<int> InsertInstanceAsync(string id, DateTimeOffset heartbeat, params string[] queues) => ExecuteAsync(
        $"INSERT INTO {Schema}.instances VALUES ('{id}', 'shipping', '{id.Split(':')[0]}', '1.0.0', " +
        $"ARRAY[{string.Join(", ", queues.Select(queue => $"'{queue}'"))}]::text[], '[]', {Literal(heartbeat)}, {Literal(heartbeat)})");

    private static string Literal(DateTimeOffset? moment) => moment is { } value ? $"'{value:O}'::timestamptz" : "NULL";
}
