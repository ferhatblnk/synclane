using SyncLane.Monitoring;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Each owner's jobs counted by status across the queues that share owner keys.</summary>
public class JobOwnerTests : PostgresTest
{
    private const string Stock = "stock";

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task Owners_are_counted_by_status_across_the_queues_that_share_their_keys_newest_job_first()
    {
        await InsertRowAsync("c-1", JobMode.Initial, JobStatus.Completed, Noon.AddDays(-3), key: "1", completedAt: Noon.AddDays(-3));
        await InsertRowAsync("c-1", JobMode.Recurring, JobStatus.Failed, Noon.AddHours(-2), key: "2");
        await InsertRowAsync("c-1", JobMode.Recurring, JobStatus.Running, Noon.AddHours(-1), key: "1", queue: Stock);
        await InsertRowAsync("c-1", JobMode.Recurring, JobStatus.Pending, Noon.AddHours(-1), key: "2", queue: Stock);
        await InsertRowAsync("c-2", JobMode.Recurring, JobStatus.Deferred, Noon.AddHours(-5), key: "1");
        await InsertRowAsync("c-2", JobMode.Recurring, JobStatus.Abandoned, Noon.AddMinutes(-30), key: "1", queue: Stock);
        await InsertRowAsync("c-3", JobMode.Recurring, JobStatus.Pending, Noon.AddHours(-8), key: "1");
        await InsertRowAsync("c-4", JobMode.Recurring, JobStatus.Pending, Noon, key: "1", queue: "billing");
        await InsertOwnerAsync(Queue, "c-1", "Acme Toys", Noon.AddDays(-1));
        await InsertOwnerAsync(Stock, "c-1", "Acme Toys GmbH", Noon);
        await InsertOwnerAsync(Stock, "c-2", null, Noon);
        await InsertOwnerAsync(Queue, "c-3", null, Noon);
        await InsertOwnerAsync("billing", "c-4", "Initech", Noon);
        await InsertOwnerAsync(Queue, "c-5", "No jobs left", Noon);

        var page = await Monitor().GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock] });

        Assert.True(page.Succeeded);
        Assert.Null(page.Total);
        Assert.Null(page.NextCursor);
        Assert.Equal(["c-2", "c-1", "c-3"], page.Items.Select(owner => owner.Owner));
        Assert.Equal(
            new JobOwnerSummary { Owner = "c-1", OwnerName = "Acme Toys GmbH", Pending = 1, Running = 1, Completed = 1, Failed = 1, Total = 4, LastJobAt = Noon.AddHours(-1) },
            page.Items[1]);
        Assert.Equal(
            new JobOwnerSummary { Owner = "c-2", Deferred = 1, Abandoned = 1, Total = 2, LastJobAt = Noon.AddMinutes(-30) },
            page.Items[0]);
        Assert.Equal((1, 1, Noon.AddHours(-8)), (page.Items[2].Pending, page.Items[2].Total, page.Items[2].LastJobAt));
    }

    [PostgresFact]
    public async Task Status_and_type_filter_the_counts_the_newest_job_and_the_owners_listed()
    {
        await InsertRowAsync("c-1", JobMode.Recurring, JobStatus.Failed, Noon.AddHours(-3), key: "1");
        await InsertRowAsync("c-1", JobMode.Recurring, JobStatus.Failed, Noon.AddHours(-2), key: "1", type: "products");
        await InsertRowAsync("c-1", JobMode.Recurring, JobStatus.Pending, Noon.AddHours(-1), key: "2");
        await InsertRowAsync("c-2", JobMode.Recurring, JobStatus.Failed, Noon.AddHours(-4), key: "1", queue: Stock);
        await InsertRowAsync("c-2", JobMode.Recurring, JobStatus.Completed, Noon, key: "2", queue: Stock, completedAt: Noon);
        await InsertRowAsync("c-3", JobMode.Recurring, JobStatus.Completed, Noon, key: "1", completedAt: Noon);
        await InsertOwnerAsync(Queue, "c-1", null, Noon);
        await InsertOwnerAsync(Stock, "c-2", null, Noon);
        await InsertOwnerAsync(Queue, "c-3", null, Noon);
        var monitor = Monitor();

        var failed = (await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock], Status = JobStatus.Failed, IncludeTotal = true }));
        var failedOrders = (await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock], Status = JobStatus.Failed, Type = "orders" })).Items;
        var products = (await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock], Type = "products" })).Items;

        Assert.Equal(2L, failed.Total);
        Assert.Equal([("c-1", 2, 2, Noon.AddHours(-2)), ("c-2", 1, 1, Noon.AddHours(-4))],
            failed.Items.Select(owner => (owner.Owner, owner.Failed, owner.Total, owner.LastJobAt)));
        Assert.Equal([("c-1", Noon.AddHours(-3)), ("c-2", Noon.AddHours(-4))], failedOrders.Select(owner => (owner.Owner, owner.LastJobAt)));
        var only = Assert.Single(products);
        Assert.Equal(("c-1", 1, 1, 0), (only.Owner, only.Failed, only.Total, only.Pending));
    }

    [PostgresFact]
    public async Task Pages_follow_the_cursor_without_skipping_or_repeating_an_owner_and_count_the_total_on_request()
    {
        // Two pairs of owners whose newest jobs were created at the same moment: the key breaks the tie.
        string[] owners = ["c-1", "c-2", "c-3", "c-4", "c-5"];
        DateTimeOffset[] newest = [Noon, Noon.AddHours(-1), Noon.AddHours(-1), Noon.AddHours(-2), Noon.AddHours(-2)];
        for (var index = 0; index < owners.Length; index++)
        {
            await InsertRowAsync(owners[index], JobMode.Recurring, JobStatus.Completed, newest[index].AddDays(-1), key: "old", completedAt: Noon);
            await InsertRowAsync(owners[index], JobMode.Recurring, JobStatus.Pending, newest[index], key: "new", queue: index % 2 == 0 ? Queue : Stock);
            await InsertOwnerAsync(Queue, owners[index], null, Noon);
            await InsertOwnerAsync(Stock, owners[index], null, Noon);
        }

        var monitor = Monitor();
        var seen = new List<string>();
        var totals = new List<long?>();
        string? cursor = null;
        do
        {
            var page = await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock], Limit = 2, Cursor = cursor, IncludeTotal = cursor is null });
            Assert.True(page.Succeeded);
            seen.AddRange(page.Items.Select(owner => owner.Owner));
            totals.Add(page.Total);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(["c-1", "c-2", "c-3", "c-4", "c-5"], seen);
        Assert.Equal([5L, null, null], totals);
        Assert.All((await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock] })).Items, owner => Assert.Equal(2, owner.Total));
    }

    [PostgresFact]
    public async Task A_search_finds_owners_by_key_or_name_on_any_of_the_queues_and_counts_all_their_jobs()
    {
        await InsertRowAsync("c-1:us", JobMode.Recurring, JobStatus.Pending, Noon, key: "1");
        await InsertRowAsync("c-1:us", JobMode.Recurring, JobStatus.Pending, Noon, key: "1", queue: Stock);
        await InsertRowAsync("c-2:de", JobMode.Recurring, JobStatus.Pending, Noon, key: "1", queue: Stock);
        await InsertRowAsync("100%_x", JobMode.Recurring, JobStatus.Pending, Noon, key: "1");
        await InsertRowAsync("100ab", JobMode.Recurring, JobStatus.Pending, Noon, key: "1");
        await InsertOwnerAsync(Queue, "c-1:us", "Acme Toys", Noon);
        await InsertOwnerAsync(Stock, "c-1:us", null, Noon);
        await InsertOwnerAsync(Stock, "c-2:de", "Initech", Noon);
        await InsertOwnerAsync(Queue, "100%_x", null, Noon);
        await InsertOwnerAsync(Queue, "100ab", null, Noon);
        var monitor = Monitor();

        var byName = await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock], Search = "ACME", IncludeTotal = true });
        var byKey = (await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue, Stock], Search = "2:D" })).Items;
        var literal = (await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue], Search = "0%_" })).Items;
        var otherQueue = (await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [Queue], Search = "initech" })).Items;

        var acme = Assert.Single(byName.Items);
        Assert.Equal(("c-1:us", "Acme Toys", 2, 1L), (acme.Owner, acme.OwnerName, acme.Total, byName.Total));
        Assert.Equal(["c-2:de"], byKey.Select(owner => owner.Owner));
        Assert.Equal(["100%_x"], literal.Select(owner => owner.Owner));
        Assert.Empty(otherQueue);
    }

    [PostgresFact]
    public async Task Bad_input_is_refused_with_its_code_and_nothing_is_read()
    {
        var monitor = Monitor();

        async Task<string?> CodeOf(JobOwnerQuery query) => (await monitor.GetJobOwnersAsync(query)).Error?.Code;

        Assert.Equal(SyncErrorCodes.QueuesInvalid, await CodeOf(new JobOwnerQuery()));
        Assert.Equal(SyncErrorCodes.QueuesInvalid, await CodeOf(new JobOwnerQuery { Queues = [] }));
        Assert.Equal(SyncErrorCodes.QueuesInvalid, await CodeOf(new JobOwnerQuery { Queues = [Queue, " "] }));
        Assert.Equal(SyncErrorCodes.LimitOutOfRange, await CodeOf(new JobOwnerQuery { Queues = [Queue], Limit = 0 }));
        Assert.Equal(SyncErrorCodes.LimitOutOfRange, await CodeOf(new JobOwnerQuery { Queues = [Queue], Limit = JobQuery.MaxLimit + 1 }));
        Assert.Equal(SyncErrorCodes.SearchInvalid, await CodeOf(new JobOwnerQuery { Queues = [Queue], Search = " " }));
        Assert.Equal(SyncErrorCodes.SearchInvalid, await CodeOf(new JobOwnerQuery { Queues = [Queue], Search = new string('a', JobQuery.MaxSearchLength + 1) }));
        Assert.Equal(SyncErrorCodes.CursorInvalid, await CodeOf(new JobOwnerQuery { Queues = [Queue], Cursor = "not-a-cursor" }));
        Assert.Equal(SyncErrorCodes.CursorInvalid, await CodeOf(new JobOwnerQuery { Queues = [Queue], Cursor = KeysetCursor.Encode("soon", "c-1") }));
        Assert.Equal(SyncErrorCodes.CursorInvalid, await CodeOf(new JobOwnerQuery { Queues = [Queue], Cursor = KeysetCursor.Encode("1", "c-1", "orders") }));
        Assert.Null(await CodeOf(new JobOwnerQuery { Queues = [Queue, Queue], Limit = JobQuery.MaxLimit, Search = new string('a', JobQuery.MaxSearchLength) }));
        Assert.Equal(
            SyncErrorKind.Validation,
            (await monitor.GetJobOwnersAsync(new JobOwnerQuery { Queues = [""] })).Error!.Kind);
    }

    private SyncLaneMonitor Monitor() => MonitorTests.MonitorFor(Store, Schema, _clock);

    private Task<int> InsertOwnerAsync(string queue, string owner, string? name, DateTimeOffset updatedAt) => ExecuteAsync(
        $"INSERT INTO {Schema}.owners (queue, owner, name, updated_at) VALUES ('{queue}', '{owner}', {(name is null ? "NULL" : $"'{name}'")}, '{updatedAt:O}'::timestamptz)");
}
