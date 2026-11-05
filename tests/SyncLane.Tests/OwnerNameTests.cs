using SyncLane.Monitoring;
using SyncLane.Planning;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>What people call an owner: given by the owner source or an enqueue, shown and searched by the monitor.</summary>
public class OwnerNameTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_name_from_the_owner_source_reaches_the_job_list_the_job_a_worker_takes_and_freshness()
    {
        await PlanAsync(new JobOwner("c-1:us", Name: "Acme Toys · US"), new JobOwner("c-2:de"));

        var listed = await JobsAsync();
        var named = listed.First(job => job.Owner == "c-1:us");
        Job?[] taken = [await LeaseAsync("w-1", _clock.GetUtcNow()), await LeaseAsync("w-2", _clock.GetUtcNow())];
        var fresh = (await Monitor().GetFreshnessAsync(new FreshnessQuery { Queue = Queue })).Items;

        Assert.All(listed.Where(job => job.Owner == "c-1:us"), job => Assert.Equal("Acme Toys · US", job.OwnerName));
        Assert.All(listed.Where(job => job.Owner == "c-2:de"), job => Assert.Null(job.OwnerName));
        Assert.Equal("Acme Toys · US", (await JobAsync(named.Id)).OwnerName);
        Assert.Equal("Acme Toys · US", taken.Single(job => job!.Owner == "c-1:us")!.OwnerName);
        Assert.Null(taken.Single(job => job!.Owner == "c-2:de")!.OwnerName);
        Assert.All(fresh.Where(row => row.Owner == "c-1:us"), row => Assert.Equal("Acme Toys · US", row.OwnerName));
    }

    [PostgresFact]
    public async Task A_new_name_replaces_the_old_one_a_missing_name_keeps_it_and_the_same_name_writes_nothing()
    {
        await PlanAsync(new JobOwner("c-1:us", Name: "Acme"));
        var first = await UpdatedAtAsync("c-1:us");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync(new JobOwner("c-1:us", Name: "  Acme  "));
        var unchanged = await UpdatedAtAsync("c-1:us");

        await PlanAsync(new JobOwner("c-1:us"));
        var kept = await NameAsync("c-1:us");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync(new JobOwner("c-1:us", Name: "Acme Toys"));

        Assert.Equal(first, unchanged);
        Assert.Equal("Acme", kept);
        Assert.Equal("Acme Toys", await NameAsync("c-1:us"));
        Assert.Equal(_clock.GetUtcNow(), await UpdatedAtAsync("c-1:us"));
    }

    [PostgresFact]
    public async Task A_source_that_lists_an_owner_twice_keeps_the_last_name_it_gave()
    {
        await PlanAsync(new JobOwner("c-1:us", Name: "Old"), new JobOwner("c-1:us", Name: "New"), new JobOwner("c-1:us"));

        Assert.Equal("New", await NameAsync("c-1:us"));
    }

    [PostgresFact]
    public async Task A_search_finds_owners_by_part_of_their_name_or_key_ignoring_case()
    {
        await PlanAsync(
            new JobOwner("c-1:us", Name: "Acme Toys · US"),
            new JobOwner("c-1:de", Name: "Acme Toys · DE"),
            new JobOwner("c-2:us", Name: "Globex"),
            new JobOwner("c-3:us"));
        var client = Client();

        var byNameResult = await client.GetJobsAsync(new JobQuery { Search = "acme", IncludeTotal = true });
        var byName = byNameResult.Items;
        var byKey = (await client.GetJobsAsync(new JobQuery { Queue = Queue, Search = "C-3" })).Items;
        var narrowed = (await client.GetJobsAsync(new JobQuery { Search = "ACME", Type = "products" })).Items;
        var fresh = (await Monitor().GetFreshnessAsync(new FreshnessQuery { Search = "toys · de" })).Items;
        var nobody = (await client.GetJobsAsync(new JobQuery { Search = "initech" })).Items;

        Assert.Equal(["c-1:de", "c-1:us"], byName.Select(job => job.Owner).Distinct().Order());
        Assert.Equal(4, byName.Count);
        Assert.Equal(4, byNameResult.Total);
        Assert.Equal(["c-3:us"], byKey.Select(job => job.Owner).Distinct());
        Assert.All(narrowed, job => Assert.Equal("products", job.Type));
        Assert.Equal(2, narrowed.Count);
        Assert.Equal(["c-1:de", "c-1:de"], fresh.Select(row => row.Owner));
        Assert.Empty(nobody);
    }

    [PostgresFact]
    public async Task A_search_pages_through_the_jobs_of_every_owner_it_finds_newest_first()
    {
        await PlanAsync(new JobOwner("c-1:us", Name: "Acme · US"), new JobOwner("c-1:de", Name: "Acme · DE"), new JobOwner("c-2:us", Name: "Globex"));
        var client = Client();
        var expected = (await JobsAsync()).Where(job => job.OwnerName?.StartsWith("Acme", StringComparison.Ordinal) == true)
            .Select(job => job.Id).OrderDescending().ToList();

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = await client.GetJobsAsync(new JobQuery { Search = "acme", Limit = 1, Cursor = cursor });
            seen.AddRange(page.Items.Select(job => job.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(4, expected.Count);
        Assert.Equal(expected, seen);
    }

    [PostgresFact]
    public async Task Percent_and_underscore_in_a_search_match_only_themselves()
    {
        await PlanAsync(new JobOwner("a", Name: "100% cotton"), new JobOwner("b", Name: "1000 cotton"), new JobOwner("c", Name: "snake_case"), new JobOwner("d", Name: "snakeXcase"));
        var client = Client();

        var percent = (await client.GetJobsAsync(new JobQuery { Search = "100%" })).Items;
        var underscore = (await client.GetJobsAsync(new JobQuery { Search = "e_c" })).Items;
        var backslash = (await client.GetJobsAsync(new JobQuery { Search = @"\" })).Items;

        Assert.Equal(["a"], percent.Select(job => job.Owner).Distinct());
        Assert.Equal(["c"], underscore.Select(job => job.Owner).Distinct());
        Assert.Empty(backslash);
    }

    [PostgresFact]
    public async Task An_empty_or_too_long_search_is_refused()
    {
        var client = Client();

        var empty = await client.GetJobsAsync(new JobQuery { Search = "  " });
        var tooLong = await client.GetJobsAsync(new JobQuery { Search = new string('a', JobQuery.MaxSearchLength + 1) });
        var longest = await client.GetJobsAsync(new JobQuery { Search = new string('a', JobQuery.MaxSearchLength) });
        var fresh = await Monitor().GetFreshnessAsync(new FreshnessQuery { Search = "" });

        Assert.Equal((SyncErrorCodes.SearchInvalid, SyncErrorKind.Validation), (empty.Error!.Code, empty.Error.Kind));
        Assert.Equal(SyncErrorCodes.SearchInvalid, tooLong.Error!.Code);
        Assert.True(longest.Succeeded);
        Assert.Equal(SyncErrorCodes.SearchInvalid, fresh.Error!.Code);
    }

    [PostgresFact]
    public async Task An_enqueued_job_names_its_owner_and_one_without_a_name_keeps_the_stored_one()
    {
        var client = Client();

        (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("c-9:jp", Name: "Initech · JP"), Type = "refresh" })).ThrowIfFailed();
        (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("c-9:jp"), Type = "refresh" })).ThrowIfFailed();
        (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("c-8:jp"), Type = "refresh" })).ThrowIfFailed();

        var found = (await client.GetJobsAsync(new JobQuery { Search = "initech" })).Items;
        var unnamed = (await client.GetJobsAsync(new JobQuery { Search = "c-8" })).Items;

        Assert.Equal(2, found.Count);
        Assert.All(found, job => Assert.Equal("Initech · JP", job.OwnerName));
        Assert.Null(Assert.Single(unnamed).OwnerName);
    }

    [PostgresFact]
    public async Task Forgetting_an_owner_forgets_its_name()
    {
        await PlanAsync(new JobOwner("c-1:us", Name: "Acme"));

        (await Client().ForgetOwnerAsync(Queue, "c-1:us")).ThrowIfFailed();

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.owners"));
    }

    [PostgresFact]
    public async Task The_migration_remembers_the_owners_of_existing_jobs_without_names()
    {
        await InsertRowAsync("old-1", JobMode.Manual, JobStatus.Completed, Noon, key: "a", completedAt: Noon);
        await InsertRowAsync("old-2", JobMode.Recurring, JobStatus.Pending, Noon, key: "b", queue: "billing");
        await ExecuteAsync($"INSERT INTO {Schema}.coverage (queue, owner, type) VALUES ('{Queue}', 'old-3', 'orders')");

        await ExecuteAsync(Migrations.All.Single(migration => migration.Version == 10).Sql(Schema));
        await ExecuteAsync(Migrations.All.Single(migration => migration.Version == 10).Sql(Schema));

        Assert.Equal(3L, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.owners WHERE name IS NULL"));
        Assert.Equal(["old-2"], (await Client().GetJobsAsync(new JobQuery { Search = "OLD-2" })).Items.Select(job => job.Owner));
    }

    private Task<int> PlanAsync(params JobOwner[] owners) =>
        new QueuePlanner(Store, _clock).PlanAsync(Options().Queue(Queue), owners, CancellationToken.None);

    private Task<string?> NameAsync(string owner) =>
        ScalarAsync<string>($"SELECT name FROM {Schema}.owners WHERE queue = '{Queue}' AND owner = '{owner}'");

    private Task<DateTime> UpdatedAtAsync(string owner) =>
        ScalarAsync<DateTime>($"SELECT updated_at FROM {Schema}.owners WHERE queue = '{Queue}' AND owner = '{owner}'");

    private static SyncLaneOptions Options() =>
        new SyncLaneOptions().AddQueue(Queue, queue => queue
            .Snapshot<OrderHandler>("orders")
            .Snapshot<ProductHandler>("products")
            .Handle<ProductHandler>("refresh"));

    private SyncLaneClient Client() => new(Options(), Store, new QueuePlanner(Store, _clock), _clock);

    private SyncLaneMonitor Monitor() => MonitorTests.MonitorFor(Store, Schema, _clock);
}
