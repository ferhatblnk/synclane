using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Moving to SyncLane from another system, and letting an owner go for good.</summary>
public class OwnerHistoryTests : PostgresTest
{
    private static readonly DateTimeOffset Today = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly JobOwner Owner = new("store-1");

    private readonly ManualClock _clock = new(Today.AddHours(9));

    [PostgresFact]
    public async Task History_marked_as_loaded_is_not_fetched_again_only_what_is_older_and_what_came_after()
    {
        var client = Client();
        var range = new LoadedRange { Owner = Owner, Type = "orders", Start = Today.AddDays(-200), End = Today.AddHours(8) };

        var marked = await client.MarkLoadedAsync(Queue, [range]);
        var markedAgain = await client.MarkLoadedAsync(Queue, [range]);
        var planned = await client.PlanAsync(Queue, Owner);

        // Products and refunds have nothing marked, so they are planned in full: one snapshot and three slices.
        var orders = (await JobsAsync(JobStatus.Pending)).Where(job => job.Type == "orders").ToList();
        var history = orders.Where(job => job.Mode == JobMode.Initial).OrderBy(job => job.WindowStart).ToList();
        var recurring = Assert.Single(orders, job => job.Mode == JobMode.Recurring);
        Assert.Equal((1, 0, 6 + 1 + 1 + 3), (marked.GetValueOrThrow(), markedAgain.GetValueOrThrow(), planned.GetValueOrThrow()));
        Assert.Equal(6, history.Count);
        Assert.Equal((Today.AddDays(-365), Today.AddDays(-200)), (history[0].WindowStart, history[^1].WindowEnd));
        Assert.Equal((Today.AddHours(8).AddMinutes(-5), Today.AddHours(9)), (recurring.WindowStart, recurring.WindowEnd));
        Assert.Equal(new OwnerProgress(11, 1, 10, 0), (await client.GetProgressAsync(Queue, Owner.Key)).GetValueOrThrow());
    }

    [PostgresFact]
    public async Task One_bad_range_marks_nothing_and_every_problem_is_reported()
    {
        var client = Client();
        LoadedRange Range(string type, DateTimeOffset start, DateTimeOffset end) => new() { Owner = Owner, Type = type, Start = start, End = end };

        var result = await client.MarkLoadedAsync(Queue,
        [
            Range("orders", Today.AddDays(-30), Today),
            Range("products", Today.AddDays(-30), Today),
            Range("invoices", Today.AddDays(-30), Today),
            Range("orders", Today, Today.AddDays(-30)),
            Range("orders", Today.AddDays(-30), Today.AddDays(1)),
            Range("refunds", Today.AddDays(-30), Today.AddDays(-1))
        ]);

        Assert.False(result.Succeeded);
        Assert.Equal(
            [SyncErrorCodes.JobTypeNotWindowed, SyncErrorCodes.JobTypeNotDeclared, SyncErrorCodes.LoadedRangeInvalid, SyncErrorCodes.LoadedRangeInvalid, SyncErrorCodes.LoadedRangeInvalid],
            result.Errors.Select(error => error.Code));
        Assert.Contains("store-1", result.Errors[^1].Message);
        Assert.Empty(await JobsAsync());
    }

    [PostgresFact]
    public async Task History_of_a_type_without_every_can_be_marked_up_to_today()
    {
        var result = await Client().MarkLoadedAsync(Queue,
            [new LoadedRange { Owner = Owner, Type = "refunds", Start = Today.AddDays(-30), End = Today.AddHours(1) }]);

        Assert.Equal(1, result.GetValueOrThrow());
    }

    [PostgresFact]
    public async Task A_forgotten_owner_has_nothing_left_and_loads_its_history_again_when_it_comes_back()
    {
        var client = Client();
        var other = new JobOwner("store-2");
        var first = await client.PlanAsync(Queue, Owner);
        await client.PlanAsync(Queue, other);
        await ExecuteAsync($"UPDATE {Schema}.jobs SET status = 'completed', completed_at = now()");

        var forgotten = await client.ForgetOwnerAsync(Queue, Owner.Key);
        var left = await JobsAsync();
        var summaries = await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.coverage WHERE owner = 'store-1'");
        var back = await client.PlanAsync(Queue, Owner);

        Assert.Equal(first.GetValueOrThrow(), forgotten.GetValueOrThrow());
        Assert.All(left, job => Assert.Equal("store-2", job.Owner));
        Assert.Equal(0, summaries);
        Assert.Equal(first.GetValueOrThrow(), back.GetValueOrThrow());
    }

    [PostgresFact]
    public async Task A_job_running_when_its_owner_is_forgotten_loses_its_lease()
    {
        var client = Client();
        await client.PlanAsync(Queue, Owner);
        var running = await LeaseAsync("w", _clock.GetUtcNow()) ?? throw new InvalidOperationException("No job is due.");

        await client.ForgetOwnerAsync(Queue, Owner.Key);
        var saved = await Store.CheckpointAsync(running.Id, "w", "page-2", 10, _clock.GetUtcNow(), _clock.GetUtcNow().AddMinutes(5), CancellationToken.None);

        Assert.False(saved);
    }

    private SyncLaneClient Client()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue
            .Snapshot<ProductHandler>("products")
            .Windowed<OrderHandler>("orders", TimeSpan.FromDays(365), TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15), overlap: TimeSpan.FromMinutes(5))
            .Windowed<OrderHandler>("refunds", TimeSpan.FromDays(90), TimeSpan.FromDays(30)));
        return new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock);
    }
}
