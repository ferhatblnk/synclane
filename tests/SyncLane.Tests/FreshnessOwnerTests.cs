using SyncLane.Monitoring;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Freshness grouped by owner across the queues that share owner keys, so a page never cuts an owner in half.</summary>
public class FreshnessOwnerTests : PostgresTest
{
    private const string Products = "stores.products";

    private static readonly string[] Both = [Queue, Products];

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task Owners_are_grouped_across_the_given_queues_stalest_owner_first_with_their_rows_oldest_first()
    {
        await InsertCoverageAsync(Queue, "a", "orders", Noon.AddDays(-3));
        await InsertCoverageAsync(Products, "a", "products", Noon.AddHours(-1));
        await InsertCoverageAsync(Queue, "b", "orders", null);
        await InsertCoverageAsync(Queue, "c", "orders", Noon.AddHours(-2));
        await InsertCoverageAsync("billing", "a", "invoices", null);
        await InsertJobAsync(Queue, "a", "orders", JobStatus.Abandoned, "JobAbandonedException: SL5106: The API answered 401.");
        await InsertRowAsync("b", JobMode.Initial, JobStatus.Completed, Noon, key: "s1", completedAt: Noon);
        await InsertRowAsync("b", JobMode.Initial, JobStatus.Pending, Noon, key: "s2");
        await InsertNameAsync(Products, "a", "Acme Toys");

        var result = await Monitor().GetFreshnessOwnersAsync(new FreshnessOwnerQuery { Queues = Both });

        var groups = result.Items;
        Assert.Equal(["b", "a", "c"], groups.Select(group => group.Owner));
        Assert.Null(result.NextCursor);
        Assert.Null(result.Total);
        Assert.Equal([(Queue, "orders"), (Products, "products")], groups[1].Rows.Select(row => (row.Queue, row.Type)));
        Assert.Equal("Acme Toys", groups[1].OwnerName);
        Assert.Equal([null, "Acme Toys"], groups[1].Rows.Select(row => row.OwnerName));
        Assert.Equal((JobStatus.Abandoned, "SL5106", Noon.AddDays(-3)), (groups[1].Rows[0].Status, groups[1].Rows[0].Code, groups[1].Rows[0].DataUntil));
        Assert.Equal((2, 1, JobStatus.Pending), (groups[0].Rows[0].HistoryTotal, groups[0].Rows[0].HistoryCompleted, groups[0].Rows[0].Status));
        Assert.DoesNotContain(groups.SelectMany(group => group.Rows), row => row.Queue == "billing");
    }

    [PostgresFact]
    public async Task Pages_hold_whole_owners_follow_the_cursor_and_count_the_owners_when_asked()
    {
        for (var index = 0; index < 5; index++)
        {
            await InsertCoverageAsync(Queue, $"owner-{index}", "orders", Noon.AddHours(-index));
            await InsertCoverageAsync(Products, $"owner-{index}", "products", Noon.AddDays(-1));
        }

        var monitor = Monitor();

        var pages = new List<SyncListResult<OwnerFreshnessGroup>>();
        string? cursor = null;
        do
        {
            var page = await monitor.GetFreshnessOwnersAsync(
                new FreshnessOwnerQuery { Queues = Both, Limit = 2, Cursor = cursor, IncludeTotal = cursor is null });
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal([2, 2, 1], pages.Select(page => page.Items.Count));
        Assert.Equal(["owner-0", "owner-1", "owner-2", "owner-3", "owner-4"], pages.SelectMany(page => page.Items).Select(group => group.Owner));
        Assert.All(pages.SelectMany(page => page.Items), group => Assert.Equal([Products, Queue], group.Rows.Select(row => row.Queue)));
        Assert.Equal([5L, null, null], pages.Select(page => page.Total));
    }

    [PostgresFact]
    public async Task Show_keeps_only_the_stale_attention_or_paused_rows_and_the_owners_that_have_them()
    {
        await InsertCoverageAsync(Queue, "fresh", "orders", Noon.AddMinutes(-10));
        await InsertJobAsync(Queue, "fresh", "orders", JobStatus.Pending, null);
        await InsertCoverageAsync(Queue, "old", "orders", Noon.AddDays(-2));
        await InsertCoverageAsync(Products, "old", "products", Noon.AddMinutes(-10));
        await InsertCoverageAsync(Queue, "never", "orders", null);
        await InsertCoverageAsync(Queue, "failing", "orders", Noon.AddMinutes(-5));
        await InsertCoverageAsync(Products, "failing", "products", Noon.AddMinutes(-5));
        await InsertJobAsync(Products, "failing", "products", JobStatus.Deferred, "HttpRequestException: SL5109: unreachable");
        await InsertJobAsync(Queue, "failing", "orders", JobStatus.Completed, "TimeoutException: old");
        await InsertCoverageAsync(Queue, "held", "orders", Noon.AddMinutes(-1));
        await InsertCoverageAsync(Products, "held", "products", Noon.AddMinutes(-1));
        await ExecuteAsync($"INSERT INTO {Schema}.pauses (queue, owner, paused_by, reason, paused_at) VALUES ('{Products}', 'held', 'ops', 'moving', now())");
        await ExecuteAsync($"INSERT INTO {Schema}.pauses (queue, owner, paused_by, reason, paused_at, until) VALUES ('{Queue}', 'fresh', 'ops', 'over', now(), '{Noon.AddMinutes(-1):O}')");
        var monitor = Monitor();

        // Each owner with the queues of its rows, in the order they come.
        async Task<List<string>> ShowAsync(FreshnessShow show, TimeSpan? staleAfter = null)
        {
            var query = new FreshnessOwnerQuery { Queues = Both, Show = show, IncludeTotal = true };
            var result = await monitor.GetFreshnessOwnersAsync(staleAfter is { } age ? query with { StaleAfter = age } : query);
            Assert.Equal(result.Items.Count, result.Total);
            return result.Items.Select(group => $"{group.Owner}: {string.Join(", ", group.Rows.Select(row => row.Queue))}").ToList();
        }

        Assert.Equal(["never: stores", "old: stores"], await ShowAsync(FreshnessShow.Stale));
        Assert.Equal(
            ["never: stores", "old: stores, stores.products", "fresh: stores", "failing: stores, stores.products"],
            await ShowAsync(FreshnessShow.Stale, TimeSpan.FromMinutes(4)));
        Assert.Equal(["never: stores"], await ShowAsync(FreshnessShow.Stale, TimeSpan.MaxValue));
        Assert.Equal(["failing: stores.products"], await ShowAsync(FreshnessShow.Attention));
        Assert.Equal(["held: stores.products"], await ShowAsync(FreshnessShow.Paused));
        Assert.Equal(5, (await ShowAsync(FreshnessShow.All)).Count);
    }

    [PostgresFact]
    public async Task A_search_finds_an_owner_by_key_or_by_a_name_on_any_queue_and_brings_all_its_rows()
    {
        await InsertCoverageAsync(Queue, "c-1", "orders", Noon.AddHours(-1));
        await InsertCoverageAsync(Products, "c-1", "products", Noon.AddHours(-2));
        await InsertCoverageAsync(Queue, "c-2", "orders", Noon.AddHours(-3));
        await InsertCoverageAsync("billing", "c-3", "invoices", Noon.AddHours(-3));
        await InsertNameAsync(Products, "c-1", "Acme Toys");
        await InsertNameAsync(Queue, "c-2", null);
        await InsertNameAsync("billing", "c-3", "Acme Billing");
        var monitor = Monitor();

        var byName = await monitor.GetFreshnessOwnersAsync(new FreshnessOwnerQuery { Queues = Both, Search = "ACME", IncludeTotal = true });
        var byKey = (await monitor.GetFreshnessOwnersAsync(new FreshnessOwnerQuery { Queues = Both, Search = "c-2" })).Items;
        var underscore = (await monitor.GetFreshnessOwnersAsync(new FreshnessOwnerQuery { Queues = Both, Search = "c_" })).Items;

        var found = Assert.Single(byName.Items);
        Assert.Equal(("c-1", "Acme Toys", 1L), (found.Owner, found.OwnerName, byName.Total));
        Assert.Equal([Products, Queue], found.Rows.Select(row => row.Queue));
        Assert.Equal(["c-2"], byKey.Select(group => group.Owner));
        Assert.Empty(underscore);
    }

    [PostgresFact]
    public async Task Queues_stale_after_limit_search_and_cursor_are_checked_before_anything_is_read()
    {
        var monitor = Monitor();

        async Task<string?> CodeAsync(FreshnessOwnerQuery query) => (await monitor.GetFreshnessOwnersAsync(query)).Error?.Code;

        Assert.Equal(SyncErrorCodes.QueuesInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = [] }));
        Assert.Equal(SyncErrorCodes.QueuesInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = null! }));
        Assert.Equal(SyncErrorCodes.QueuesInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = [Queue, " "] }));
        Assert.Equal(SyncErrorCodes.StaleAfterInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = Both, StaleAfter = TimeSpan.Zero }));
        Assert.Equal(SyncErrorCodes.StaleAfterInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = Both, StaleAfter = TimeSpan.FromHours(-1) }));
        Assert.Equal(SyncErrorCodes.LimitOutOfRange, await CodeAsync(new FreshnessOwnerQuery { Queues = Both, Limit = 0 }));
        Assert.Equal(SyncErrorCodes.LimitOutOfRange, await CodeAsync(new FreshnessOwnerQuery { Queues = Both, Limit = JobQuery.MaxLimit + 1 }));
        Assert.Equal(SyncErrorCodes.SearchInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = Both, Search = " " }));
        Assert.Equal(SyncErrorCodes.CursorInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = Both, Cursor = "not-a-cursor" }));
        Assert.Equal(SyncErrorCodes.CursorInvalid, await CodeAsync(new FreshnessOwnerQuery { Queues = Both, Cursor = KeysetCursor.Encode("1", "a", "b") }));
        Assert.Null(await CodeAsync(new FreshnessOwnerQuery { Queues = [Queue, Queue], Limit = JobQuery.MaxLimit }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CodeAsync(new FreshnessOwnerQuery { Queues = Both, Show = (FreshnessShow)9 }));
    }

    private SyncLaneMonitor Monitor() => MonitorTests.MonitorFor(Store, Schema, _clock);

    private Task<int> InsertCoverageAsync(string queue, string owner, string type, DateTimeOffset? completedTo) => ExecuteAsync(
        $"INSERT INTO {Schema}.coverage (queue, owner, type, has_initial, completed_to, completed_at) VALUES " +
        $"('{queue}', '{owner}', '{type}', true, {Literal(completedTo)}, {Literal(completedTo)})");

    private Task<int> InsertNameAsync(string queue, string owner, string? name) => ExecuteAsync(
        $"INSERT INTO {Schema}.owners (queue, owner, name, updated_at) VALUES ('{queue}', '{owner}', {(name is null ? "NULL" : $"'{name}'")}, now())");

    private Task<int> InsertJobAsync(string queue, string owner, string type, JobStatus status, string? error) => ExecuteAsync(
        $"INSERT INTO {Schema}.jobs (queue, owner, lane, type, mode, status, error, dedup_key, next_run_at, created_at, updated_at) VALUES " +
        $"('{queue}', '{owner}', '{owner}', '{type}', 'recurring', '{JobText.Of(status)}', {(error is null ? "NULL" : $"'{error}'")}, '{Guid.NewGuid():N}', " +
        $"{Literal(Noon)}, {Literal(Noon)}, {Literal(Noon)})");

    private static string Literal(DateTimeOffset? moment) => moment is { } value ? $"'{value:O}'::timestamptz" : "NULL";
}
