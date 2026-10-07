using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class PlanningTests : PostgresTest
{
    private static readonly DateTimeOffset Today = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly JobOwner Owner = new("store-1");
    private static readonly TimeSpan Slice = TimeSpan.FromDays(30);

    private readonly ManualClock _clock = new(Today.AddHours(9));

    [PostgresFact]
    public async Task Planning_fills_what_is_missing_and_repeating_it_changes_nothing()
    {
        var newConnection = await PlanAsync(Orders(horizonDays: 60));
        var sameDayAgain = await PlanAsync(Orders(horizonDays: 60));
        _clock.Advance(TimeSpan.FromHours(5));
        var laterThatDay = await PlanAsync(Orders(horizonDays: 60));

        var horizonRaised = await PlanAsync(Orders(horizonDays: 730));
        var againAfterRaising = await PlanAsync(Orders(horizonDays: 730));
        _clock.Set(Today.AddDays(1).AddHours(9));
        var nextDay = await PlanAsync(Orders(horizonDays: 730));

        Assert.Equal((2, 0, 0), (newConnection, sameDayAgain, laterThatDay));
        Assert.Equal((23, 0, 0), (horizonRaised, againAfterRaising, nextDay));

        var windows = (await JobsAsync()).OrderBy(job => job.WindowStart).ToList();
        Assert.Equal(25, windows.Count);
        Assert.Equal(Today.AddDays(-730), windows[0].WindowStart);
        Assert.Equal(Today, windows[^1].WindowEnd);
        Assert.All(windows.Zip(windows.Skip(1)), pair => Assert.Equal(pair.First.WindowEnd, pair.Second.WindowStart));
    }

    [PostgresFact]
    public async Task A_window_that_was_given_up_still_counts_as_planned_and_comes_back_through_a_requeue()
    {
        await PlanAsync(Orders(horizonDays: 60));
        await ExecuteAsync($"UPDATE {Schema}.jobs SET status = 'abandoned'");

        var plannedAgain = await PlanAsync(Orders(horizonDays: 60));
        var requeued = await Store.RequeueAsync(Queue, Owner.Key, _clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(0, plannedAgain);
        Assert.Equal(2, requeued);
        Assert.Equal(2, (await JobsAsync(JobStatus.Pending)).Count);
    }

    [PostgresFact]
    public async Task Each_recurring_window_continues_where_the_last_one_ended()
    {
        var queue = Orders(horizonDays: 60, every: TimeSpan.FromMinutes(15), overlap: TimeSpan.FromMinutes(5));

        var onConnect = await PlanAsync(queue);
        var first = await TakeNewestAsync();
        await Store.CompleteAsync(first.Id, "w", _clock.GetUtcNow(), CancellationToken.None);

        var samePeriod = await PlanAtAsync(queue, 9, 10);
        var nextPeriod = await PlanAtAsync(queue, 9, 20);
        var second = await TakeNewestAsync();
        var whileOutstanding = await PlanAtAsync(queue, 11, 0);
        await Store.CompleteAsync(second.Id, "w", _clock.GetUtcNow(), CancellationToken.None);

        var afterThePause = await PlanAtAsync(queue, 12, 0);
        var third = await TakeNewestAsync();

        Assert.Equal((3, 0, 1, 0, 1), (onConnect, samePeriod, nextPeriod, whileOutstanding, afterThePause));
        Assert.All([first, second, third], job => Assert.Equal(JobMode.Recurring, job.Mode));
        Assert.Equal((Today.AddMinutes(-5), Today.AddHours(9)), (first.WindowStart, first.WindowEnd));
        Assert.Equal((Today.AddHours(9).AddMinutes(-5), Today.AddHours(9).AddMinutes(20)), (second.WindowStart, second.WindowEnd));
        Assert.Equal((Today.AddHours(9).AddMinutes(15), Today.AddHours(12)), (third.WindowStart, third.WindowEnd));
    }

    [PostgresFact]
    public async Task A_snapshot_is_refreshed_once_per_period()
    {
        var queue = Options(options => options.Snapshot<ProductHandler>("products", every: TimeSpan.FromHours(1)));

        var onConnect = await PlanAsync(queue);
        var whileLoading = await PlanAtAsync(queue, 10, 30);
        await Store.CompleteAsync((await TakeNewestAsync()).Id, "w", _clock.GetUtcNow(), CancellationToken.None);
        var nextHour = await PlanAtAsync(queue, 11, 0);
        var sameHour = await PlanAtAsync(queue, 11, 30);
        await Store.CompleteAsync((await TakeNewestAsync()).Id, "w", _clock.GetUtcNow(), CancellationToken.None);
        var sameHourAfterItRan = await PlanAtAsync(queue, 11, 45);
        var hourAfter = await PlanAtAsync(queue, 12, 0);

        Assert.Equal((1, 0, 1, 0, 0, 1), (onConnect, whileLoading, nextHour, sameHour, sameHourAfterItRan, hourAfter));
        Assert.Equal(
            [JobMode.Initial, JobMode.Recurring, JobMode.Recurring],
            (await JobsAsync()).Select(job => job.Mode));
    }

    [PostgresFact]
    public async Task Several_instances_planning_at_once_create_every_job_once()
    {
        var queue = Options(options => options
            .Snapshot<ProductHandler>("products")
            .Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), Slice, every: TimeSpan.FromMinutes(15)));
        JobOwner[] owners = [new("store-1"), new("store-2"), new("store-3")];

        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => PlanAsync(queue, owners))));

        Assert.Equal(12, created.Sum());
        Assert.Equal(12, (await JobsAsync()).Count);
    }

    [PostgresFact]
    public async Task Many_owners_are_planned_in_one_round_each_into_its_lane()
    {
        var queue = Options(options => options
            .Snapshot<ProductHandler>("products")
            .Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), Slice));
        var owners = Enumerable.Range(1, 200).Select(index => new JobOwner($"store-{index}", $"seller-{index % 50}")).ToArray();

        var created = await PlanAsync(queue, owners);
        var again = await PlanAsync(queue, owners);

        var jobs = await JobsAsync();
        Assert.Equal((600, 0), (created, again));
        Assert.Equal(50, jobs.Select(job => job.Lane).Distinct().Count());
        Assert.All(jobs.Where(job => job.Owner == "store-7"), job => Assert.Equal("seller-7", job.Lane));
    }

    private Task<int> PlanAsync(QueueOptions queue, params JobOwner[] owners) =>
        new QueuePlanner(Store, _clock).PlanAsync(queue, owners.Length == 0 ? [Owner] : owners, CancellationToken.None);

    private Task<int> PlanAtAsync(QueueOptions queue, int hour, int minute)
    {
        _clock.Set(Today.AddHours(hour).AddMinutes(minute));
        return PlanAsync(queue);
    }

    private async Task<Job> TakeNewestAsync() =>
        await LeaseAsync("w", _clock.GetUtcNow()) ?? throw new InvalidOperationException("No job is due.");

    private static QueueOptions Orders(int horizonDays, TimeSpan? every = null, TimeSpan overlap = default) =>
        Options(options => options.Windowed<OrderHandler>("orders", TimeSpan.FromDays(horizonDays), Slice, every, overlap));

    private static QueueOptions Options(Action<QueueOptions> configure) =>
        new SyncLaneOptions().AddQueue(Queue, configure).Queue(Queue);
}
