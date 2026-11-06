using Microsoft.Extensions.Logging.Abstractions;
using SyncLane.Instances;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Several applications on one schema: each runs its own queues, and none can take another's.</summary>
public class InstanceTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_second_application_cannot_run_a_queue_another_one_runs_and_is_told_which_one()
    {
        await Registry("shipping", "host-a:1", ["orders", "labels"]).StartAsync(CancellationToken.None);

        var refused = await Assert.ThrowsAsync<SyncLaneException>(() =>
            Registry("billing", "host-b:1", ["orders", "invoices", "labels"]).StartAsync(CancellationToken.None));

        Assert.Equal([SyncErrorCodes.QueueRunByAnotherApplication, SyncErrorCodes.QueueRunByAnotherApplication], refused.Errors.Select(error => error.Code));
        Assert.StartsWith("Queue 'orders' is already run by application 'shipping' (instance 'host-a:1'", refused.Errors[0].Message);
        Assert.StartsWith("Queue 'labels'", refused.Errors[1].Message);
        Assert.Equal(["host-a:1"], await IdsAsync());
    }

    [PostgresFact]
    public async Task Copies_of_one_application_share_its_queues()
    {
        await Registry("shipping", "host-a:1", ["orders"]).StartAsync(CancellationToken.None);
        await Registry("shipping", "host-b:1", ["orders"]).StartAsync(CancellationToken.None);

        Assert.Equal(["host-a:1", "host-b:1"], await IdsAsync());
    }

    [PostgresFact]
    public async Task An_instance_that_neither_plans_nor_works_claims_no_queue_but_is_listed()
    {
        await Registry("shipping", "host-a:1", ["orders"]).StartAsync(CancellationToken.None);
        await Registry("monitor", "host-m:1", ["orders"], runs: false).StartAsync(CancellationToken.None);

        Assert.Equal(["host-a:1", "host-m:1"], await IdsAsync());
        Assert.Equal(0, await ScalarAsync<int>($"SELECT cardinality(queues) FROM {Schema}.instances WHERE id = 'host-m:1'"));
    }

    [PostgresFact]
    public async Task A_queue_is_free_again_when_its_application_stops_or_falls_silent()
    {
        var shipping = Registry("shipping", "host-a:1", ["orders"]);
        await shipping.StartAsync(CancellationToken.None);
        await shipping.StopAsync(CancellationToken.None);

        await Registry("billing", "host-b:1", ["orders"]).StartAsync(CancellationToken.None);
        _clock.Advance(InstanceRules.GoneAfter + TimeSpan.FromSeconds(1));
        await Registry("returns", "host-c:1", ["orders"]).StartAsync(CancellationToken.None);

        Assert.Equal(["host-a:1", "host-b:1", "host-c:1"], await IdsAsync());
    }

    [PostgresFact]
    public async Task A_stopped_instance_stays_listed_with_when_it_stopped_until_it_is_forgotten_and_a_restart_makes_it_live()
    {
        var shipping = Registry("shipping", "host-a:1", ["orders"]);
        await shipping.StartAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(10));
        var stoppedAt = _clock.GetUtcNow();
        await shipping.StopAsync(CancellationToken.None);
        var monitor = MonitorTests.MonitorFor(Store, Schema, _clock);

        var stopped = Assert.Single((await monitor.GetInstancesAsync()).GetValueOrThrow());
        var overview = (await monitor.GetOverviewAsync()).GetValueOrThrow();

        var restarted = Registry("shipping", "host-a:1", ["orders"]);
        await restarted.StartAsync(CancellationToken.None);
        var live = Assert.Single((await monitor.GetInstancesAsync()).GetValueOrThrow());
        await restarted.StopAsync(CancellationToken.None);

        _clock.Advance(InstanceRules.ForgetAfter + TimeSpan.FromMinutes(1));
        var other = Registry("billing", "host-b:1", ["billing"]);
        await other.StartAsync(CancellationToken.None);
        await other.HeartbeatAsync(CancellationToken.None);

        Assert.Equal((stoppedAt, false), (stopped.StoppedAt, stopped.IsAlive));
        Assert.DoesNotContain(overview, queue => queue.Instances > 0);
        Assert.Equal((null, true), (live.StoppedAt, live.IsAlive));
        Assert.Equal(["host-b:1"], await IdsAsync());
    }

    [PostgresFact]
    public async Task A_heartbeat_keeps_an_instance_alive_brings_its_row_back_and_forgets_instances_silent_for_a_day()
    {
        var shipping = Registry("shipping", "host-a:1", ["orders"]);
        await shipping.StartAsync(CancellationToken.None);
        await ExecuteAsync(
            $"INSERT INTO {Schema}.instances VALUES ('long-gone:1', 'old', 'long-gone', '0.1', '{{orders}}', '[]', '{Noon.AddDays(-3):O}', '{Noon.AddDays(-2):O}')");

        _clock.Advance(TimeSpan.FromSeconds(90));
        await ExecuteAsync($"DELETE FROM {Schema}.instances WHERE id = 'host-a:1'");
        await shipping.HeartbeatAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(["host-a:1"], await IdsAsync());
        await Assert.ThrowsAsync<SyncLaneException>(() => Registry("billing", "host-b:1", ["orders"]).StartAsync(CancellationToken.None));
    }

    [PostgresFact]
    public async Task Two_applications_starting_at_the_same_moment_never_both_take_a_queue()
    {
        for (var round = 0; round < 10; round++)
        {
            await ExecuteAsync($"DELETE FROM {Schema}.instances");

            var started = await Task.WhenAll(
                StartsAsync(Registry("shipping", "host-a:1", ["orders"])),
                StartsAsync(Registry("billing", "host-b:1", ["orders"])));

            Assert.Equal(1, started.Count(ok => ok));
            Assert.Single(await IdsAsync());
        }

        static async Task<bool> StartsAsync(InstanceRegistry registry)
        {
            try
            {
                await Task.Yield();
                await registry.StartAsync(CancellationToken.None);
                return true;
            }
            catch (SyncLaneException)
            {
                return false;
            }
        }
    }

    [PostgresFact]
    public async Task A_budget_declared_differently_elsewhere_does_not_stop_the_start_and_both_declarations_are_stored()
    {
        await Registry("shipping", "host-a:1", ["orders"], budget: 10).StartAsync(CancellationToken.None);
        await Registry("billing", "host-b:1", ["invoices"], budget: 5).StartAsync(CancellationToken.None);

        var stored = await ScalarAsync<string>($"SELECT string_agg(budgets::text, ' ' ORDER BY id) FROM {Schema}.instances");
        Assert.Contains("\"limit\": 10", stored);
        Assert.Contains("\"limit\": 5", stored);
    }

    private InstanceRegistry Registry(string application, string id, string[] queues, bool runs = true, int? budget = null)
    {
        var options = new SyncLaneOptions { RunPlanner = runs, RunWorkers = runs };
        foreach (var queue in queues)
            options.AddQueue(queue, declared => declared.Handle<ProductHandler>("products"));
        if (budget is { } limit)
            options.AddBudget("api", limit, TimeSpan.FromSeconds(1));

        return new InstanceRegistry(
            options,
            new InstanceStore(Store.DataSource, new InstanceSql(Schema)),
            new InstanceIdentity(id, id.Split(':')[0], application, "1.0.0"),
            _clock,
            new Monitoring.HealthSignals(),
            NullLogger<InstanceRegistry>.Instance);
    }

    private async Task<List<string>> IdsAsync() =>
        (await ScalarAsync<string>($"SELECT coalesce(string_agg(id, ',' ORDER BY id), '') FROM {Schema}.instances"))!
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .ToList();
}
