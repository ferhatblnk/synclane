using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class JobPlannerTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Today.AddHours(14).AddMinutes(20).AddSeconds(7).AddMilliseconds(350);
    private static readonly JobOwner Owner = new("store-1");

    [Fact]
    public void A_new_owner_gets_a_snapshot_and_its_history_in_slices()
    {
        var queue = Queue(options => options
            .Snapshot<ProductHandler>("products")
            .Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30)));

        var missing = JobPlanner.Plan(queue, Owner, _ => TypeCoverage.None, Now);

        Assert.Equal(
            [
                new PlannedJob("store-1", "store-1", "products", JobMode.Initial, 0, null, null, "initial"),
                new PlannedJob("store-1", "store-1", "orders", JobMode.Initial, 1, Today.AddDays(-30), Today, "initial:2026-07-11T00:00:00Z"),
                new PlannedJob("store-1", "store-1", "orders", JobMode.Initial, 1, Today.AddDays(-60), Today.AddDays(-30), "initial:2026-06-11T00:00:00Z")
            ],
            missing);
    }

    [Fact]
    public void History_is_anchored_to_the_day_so_the_time_of_the_call_does_not_matter()
    {
        var queue = Queue(options => options.Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30)));

        var morning = JobPlanner.Plan(queue, Owner, _ => TypeCoverage.None, Today.AddHours(1));
        var evening = JobPlanner.Plan(queue, Owner, _ => TypeCoverage.None, Today.AddHours(23));

        Assert.Equal(morning, evening);
    }

    [Fact]
    public void Only_the_gap_is_planned_when_the_horizon_grows()
    {
        var queue = Queue(options => options.Windowed<OrderHandler>("orders", TimeSpan.FromDays(730), TimeSpan.FromDays(30)));
        var seen = TypeCoverage.None with { HasInitial = true, InitialFrom = Today.AddDays(-60), CoveredTo = Today };

        var missing = JobPlanner.Plan(queue, Owner, _ => seen, Now);

        Assert.Equal(23, missing.Count);
        Assert.Equal(Today.AddDays(-60), missing[0].WindowEnd);
        Assert.Equal(Today.AddDays(-730), missing[^1].WindowStart);
        Assert.All(missing.Zip(missing.Skip(1)), pair => Assert.Equal(pair.First.WindowStart, pair.Second.WindowEnd));
    }

    [Fact]
    public void Nothing_is_planned_when_the_history_already_reaches_the_horizon()
    {
        var queue = Queue(options => options.Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30)));
        var seen = TypeCoverage.None with { HasInitial = true, InitialFrom = Today.AddDays(-60), CoveredTo = Today };

        Assert.Empty(JobPlanner.Plan(queue, Owner, _ => seen, Now));
        Assert.Empty(JobPlanner.Plan(queue, Owner, _ => seen, Now.AddDays(1)));
    }

    [Fact]
    public void A_recurring_window_starts_where_coverage_ends_and_reaches_back_by_the_overlap()
    {
        var queue = Queue(options => options.Windowed<OrderHandler>(
            "orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15), overlap: TimeSpan.FromMinutes(5)));

        var recurring = Assert.Single(JobPlanner.Plan(queue, Owner, _ => TypeCoverage.None, Now), job => job.Mode == JobMode.Recurring);

        Assert.Equal(Today.AddMinutes(-5), recurring.WindowStart);
        Assert.Equal(Today.AddHours(14).AddMinutes(20).AddSeconds(7), recurring.WindowEnd);
        Assert.Equal("recurring:2026-08-10T14:15:00Z", recurring.DedupKey);
    }

    [Fact]
    public void After_an_outage_one_window_covers_everything_that_was_missed()
    {
        var queue = Queue(options => options.Windowed<OrderHandler>(
            "orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15)));
        var threeDaysAgo = Today.AddDays(-3).AddHours(9);
        var seen = Covered(to: threeDaysAgo, lastRecurringAt: threeDaysAgo);

        var recurring = Assert.Single(JobPlanner.Plan(queue, Owner, _ => seen, Now));

        Assert.Equal(threeDaysAgo, recurring.WindowStart);
        Assert.Equal(Slots.WholeSeconds(Now), recurring.WindowEnd);
    }

    [Fact]
    public void A_period_is_planned_once()
    {
        var queue = Queue(options => options.Windowed<OrderHandler>(
            "orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15)));
        var seen = Covered(to: Now.AddMinutes(-2), lastRecurringAt: Now.AddMinutes(-2));

        Assert.Empty(JobPlanner.Plan(queue, Owner, _ => seen, Now));
        Assert.Single(JobPlanner.Plan(queue, Owner, _ => seen, Now.AddMinutes(15)));
    }

    [Fact]
    public void Recurring_jobs_do_not_pile_up_behind_one_that_is_still_outstanding()
    {
        var queue = Queue(options => options
            .Snapshot<ProductHandler>("products", every: TimeSpan.FromHours(1))
            .Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15)));
        var busy = Covered(to: Today, lastRecurringAt: Today) with { AnyOutstanding = true, RecurringOutstanding = true };

        Assert.Empty(JobPlanner.Plan(queue, Owner, _ => busy, Now));
    }

    [Fact]
    public void A_snapshot_is_refreshed_once_per_period_and_not_right_after_its_first_load()
    {
        var queue = Queue(options => options.Snapshot<ProductHandler>("products", every: TimeSpan.FromHours(1)));
        var loaded = TypeCoverage.None with { HasInitial = true, LastPlannedAt = Now.AddMinutes(-5) };

        var first = JobPlanner.Plan(queue, Owner, _ => TypeCoverage.None, Now);
        var whileLoading = JobPlanner.Plan(queue, Owner, _ => loaded with { AnyOutstanding = true }, Now.AddHours(1));
        var sameHour = JobPlanner.Plan(queue, Owner, _ => loaded, Now);
        var nextHour = JobPlanner.Plan(queue, Owner, _ => loaded, Now.AddHours(1));

        Assert.Equal(JobMode.Initial, Assert.Single(first).Mode);
        Assert.Empty(whileLoading);
        Assert.Empty(sameHour);
        Assert.Equal("recurring:2026-08-10T15:00:00Z", Assert.Single(nextHour).DedupKey);
    }

    [Fact]
    public void History_still_outstanding_does_not_hold_back_what_is_new()
    {
        var queue = Queue(options => options.Windowed<OrderHandler>(
            "orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15)));
        var loading = TypeCoverage.None with { HasInitial = true, InitialFrom = Today.AddDays(-60), CoveredTo = Today, AnyOutstanding = true };

        Assert.Equal(JobMode.Recurring, Assert.Single(JobPlanner.Plan(queue, Owner, _ => loading, Now)).Mode);
    }

    [Fact]
    public void Owners_that_share_a_lane_are_planned_into_it()
    {
        var queue = Queue(options => options.Snapshot<ProductHandler>("products"));

        var planned = Assert.Single(JobPlanner.Plan(queue, new JobOwner("store-eu", "seller-7"), _ => TypeCoverage.None, Now));

        Assert.Equal("store-eu", planned.Owner);
        Assert.Equal("seller-7", planned.Lane);
    }

    [Fact]
    public void A_type_that_is_only_handled_is_never_planned()
    {
        var queue = Queue(options => options.Handle<ProductHandler>("resync"));

        Assert.Empty(JobPlanner.Plan(queue, Owner, _ => TypeCoverage.None, Now));
    }

    private static TypeCoverage Covered(DateTimeOffset to, DateTimeOffset lastRecurringAt) =>
        new(true, Today.AddDays(-60), to, false, false, lastRecurringAt, lastRecurringAt);

    private static QueueOptions Queue(Action<QueueOptions> configure) =>
        new SyncLaneOptions().AddQueue("stores", configure).Queue("stores");
}
