using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Priority levels on jobs and owners, and how they meet type order and an operator's "prioritize".</summary>
public class PriorityTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task Urgent_jobs_run_first_then_owners_an_operator_moved_forward_then_high_normal_and_low()
    {
        var client = Client();
        // Enqueued in the reverse of the expected order, so the order cannot come from the ids.
        await EnqueueAsync(client, "low", JobPriority.Low);
        await EnqueueAsync(client, "normal", JobPriority.Normal);
        await EnqueueAsync(client, "high-products", JobPriority.High, type: "products");
        await EnqueueAsync(client, "high-orders", JobPriority.High);
        await EnqueueAsync(client, "moved", JobPriority.Normal);
        await EnqueueAsync(client, "urgent", JobPriority.Urgent);
        var monitor = MonitorTests.MonitorFor(Store, Schema, _clock);

        var moved = await monitor.PrioritizeOwnerAsync(Queue, "moved");
        var urgentMoved = await monitor.PrioritizeOwnerAsync(Queue, "urgent");

        Assert.Equal((1, 0), (moved.GetValueOrThrow(), urgentMoved.GetValueOrThrow()));
        Assert.Equal(["urgent", "moved", "high-orders", "high-products", "normal", "low"], await LeaseOrderAsync(6));
    }

    [PostgresFact]
    public async Task An_owner_given_a_priority_by_its_owner_source_has_its_planned_jobs_run_first()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue.Snapshot<ProductHandler>("products"));
        var client = new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock);

        await client.PlanAsync(Queue, new JobOwner("old-customer"));
        await client.PlanAsync(Queue, new JobOwner("new-customer") { Priority = JobPriority.High });

        Assert.Equal(["new-customer", "old-customer"], await LeaseOrderAsync(2, job => job.Owner));
        Assert.Equal(
            [("new-customer", JobPriority.High), ("old-customer", JobPriority.Normal)],
            (await JobsAsync()).OrderBy(job => job.Priority).Select(job => (job.Owner, job.PriorityLevel)));
    }

    [PostgresFact]
    public async Task A_job_s_own_priority_wins_over_its_owner_s_and_an_operator_s_move_shows_on_the_job()
    {
        var client = Client();
        var owner = new JobOwner("a") { Priority = JobPriority.Low };

        var own = (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = owner, Type = "orders", Priority = JobPriority.Urgent })).GetValueOrThrow();
        var inherited = (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = owner, Type = "products" })).GetValueOrThrow();
        var high = (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("b") { Priority = JobPriority.High }, Type = "orders" })).GetValueOrThrow();
        var monitor = MonitorTests.MonitorFor(Store, Schema, _clock);
        await monitor.PrioritizeOwnerAsync(Queue, "a");
        await monitor.PrioritizeOwnerAsync(Queue, "b");

        var (first, second, third) = (await JobAsync(own), await JobAsync(inherited), await JobAsync(high));
        Assert.Equal((JobPriority.Urgent, false), (first.PriorityLevel, first.Prioritized));
        Assert.Equal((JobPriority.Low, true), (second.PriorityLevel, second.Prioritized));
        Assert.Equal((JobPriority.High, true), (third.PriorityLevel, third.Prioritized));
    }

    [PostgresFact]
    public async Task A_priority_that_is_not_a_level_is_a_bug_in_the_caller()
    {
        var job = new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders", Priority = (JobPriority)7 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client().EnqueueAsync(job));
        Assert.Empty(await JobsAsync());
    }

    private SyncLaneClient Client()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue
            .Handle<ProductHandler>("orders")
            .Handle<ProductHandler>("products"));
        return new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock);
    }

    private static async Task EnqueueAsync(SyncLaneClient client, string owner, JobPriority priority, string type = "orders") =>
        (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner(owner), Type = type, Priority = priority })).GetValueOrThrow();

    private async Task<List<string>> LeaseOrderAsync(int count, Func<Job, string>? name = null)
    {
        var order = new List<string>();

        // Sequential: each lease takes the next job in line.
        for (var slot = 0; slot < count; slot++)
            order.Add((name ?? (job => job.Owner))((await LeaseAsync($"w{slot}", Noon))!));

        return order;
    }
}

/// <summary>The stored numbers behind the levels, without a database.</summary>
public class PrioritiesTests
{
    [Theory]
    [InlineData(JobPriority.Low)]
    [InlineData(JobPriority.Normal)]
    [InlineData(JobPriority.High)]
    [InlineData(JobPriority.Urgent)]
    public void Every_level_reads_back_from_its_number_for_any_type_and_after_an_operator_s_move(JobPriority level)
    {
        foreach (var typeOrder in new[] { 0, 1, 37 })
        {
            var stored = Priorities.Of(level, typeOrder);
            Assert.Equal((level, false), (Priorities.LevelOf(stored), Priorities.IsMoved(stored)));

            if (level != JobPriority.Urgent)
                Assert.Equal((level, true), (Priorities.LevelOf(stored - Priorities.Boost), Priorities.IsMoved(stored - Priorities.Boost)));
        }
    }

    [Fact]
    public void Rows_written_before_levels_existed_read_as_normal_whether_moved_or_not()
    {
        Assert.Equal((JobPriority.Normal, false), (Priorities.LevelOf(2), Priorities.IsMoved(2)));
        Assert.Equal((JobPriority.Normal, true), (Priorities.LevelOf(2 - 1_000_000), Priorities.IsMoved(2 - 1_000_000)));
    }

    [Fact]
    public void Levels_keep_their_order_however_many_types_a_queue_has()
    {
        const int lastType = 999;

        Assert.True(Priorities.Of(JobPriority.Urgent, lastType) < Priorities.Of(JobPriority.High, 0) - Priorities.Boost);
        Assert.True(Priorities.Of(JobPriority.Low, 0) - Priorities.Boost < Priorities.Of(JobPriority.High, 0));
        Assert.True(Priorities.Of(JobPriority.High, lastType) < Priorities.Of(JobPriority.Normal, 0));
        Assert.True(Priorities.Of(JobPriority.Normal, lastType) < Priorities.Of(JobPriority.Low, 0));
    }
}
