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
    public async Task Each_instance_records_its_role_and_what_it_runs_and_the_monitor_shows_them()
    {
        await Registry("shop", "web-1:1:aaaaaa", ["orders"], runs: false).StartAsync(CancellationToken.None);
        await Registry("shop", "worker-1:1:bbbbbb", ["orders"], role: "worker").StartAsync(CancellationToken.None);

        var instances = (await MonitorTests.MonitorFor(Store, Schema, _clock).GetInstancesAsync()).GetValueOrThrow();

        Assert.Equal(
            [("web-1:1:aaaaaa", "", false, false, false), ("worker-1:1:bbbbbb", "worker", true, true, true)],
            instances.Select(i => (i.Id, i.Role, i.RunsWorkers, i.RunsPlanner, i.ListensForChanges)).ToList());
    }

    [PostgresFact]
    public async Task An_instance_of_an_older_version_is_listed_without_a_role_and_without_saying_what_it_runs()
    {
        await ExecuteAsync(
            $"INSERT INTO {Schema}.instances (id, application, host, version, queues, budgets, started_at, heartbeat_at) " +
            $"VALUES ('old:1', 'shop', 'old', '0.6.3', ARRAY['orders'], '[]', now(), now())");

        var old = Assert.Single((await MonitorTests.MonitorFor(Store, Schema, _clock).GetInstancesAsync()).GetValueOrThrow());

        Assert.Equal(("", (bool?)null, (bool?)null, (bool?)null), (old.Role, old.RunsWorkers, old.RunsPlanner, old.ListensForChanges));
        Assert.False(old.FollowsSwitches);
    }

    [PostgresFact]
    public async Task The_switches_count_the_live_instances_of_an_older_version_that_ignore_them()
    {
        await ExecuteAsync(
            $"INSERT INTO {Schema}.instances (id, application, host, version, queues, budgets, started_at, heartbeat_at) " +
            $"VALUES ('old:1', 'shop', 'old', '0.6.3', ARRAY['orders'], '[]', '{Noon:O}', '{Noon:O}')");
        await SwitchAsync("shop", "", workers: false, planner: false);
        await Registry("shop", "new-1:1:aaaaaa", ["orders"]).StartAsync(CancellationToken.None);
        var monitor = MonitorTests.MonitorFor(Store, Schema, _clock);

        var shop = Assert.Single((await monitor.GetSwitchesAsync()).GetValueOrThrow());
        var instances = (await monitor.GetInstancesAsync()).GetValueOrThrow();

        Assert.Equal((2, 0, 0, 1), (shop.LiveInstances, shop.RunningWorkers, shop.RunningPlanner, shop.IgnoringSwitch));
        Assert.Equal([("new-1:1:aaaaaa", true), ("old:1", false)], instances.Select(i => (i.Id, i.FollowsSwitches)).ToList());
    }

    [PostgresFact]
    public async Task A_switch_in_the_database_overrides_the_options_from_the_start()
    {
        await SwitchAsync("shop", "", workers: false, planner: false);
        var roles = new RoleSwitch();

        await Registry("shop", "web-1:1:aaaaaa", ["orders"], roles: roles).StartAsync(CancellationToken.None);

        Assert.Equal((false, false), (roles.Workers, roles.Planner));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT cardinality(queues) FROM {Schema}.instances"));
    }

    [PostgresFact]
    public async Task A_switch_set_while_running_takes_effect_with_the_next_heartbeat_and_claims_the_queues()
    {
        var roles = new RoleSwitch();
        var registry = Registry("shop", "worker-1:1:aaaaaa", ["orders"], runs: false, roles: roles);
        await registry.StartAsync(CancellationToken.None);
        var before = (roles.Workers, roles.Planner);

        await SwitchAsync("shop", "", workers: true, planner: true);
        await registry.HeartbeatAsync(CancellationToken.None);

        Assert.Equal((false, false), before);
        Assert.Equal((true, true), (roles.Workers, roles.Planner));
        Assert.Equal(["orders"], (await ScalarAsync<string[]>($"SELECT queues FROM {Schema}.instances"))!);
    }

    [PostgresFact]
    public async Task A_switch_covers_one_role_and_a_value_left_empty_keeps_the_options()
    {
        await ExecuteAsync(
            $"INSERT INTO {Schema}.switches (application, role, run_workers, run_planner, changed_at) VALUES ('shop', 'worker', NULL, false, now())");
        var web = new RoleSwitch();
        var worker = new RoleSwitch();

        await Registry("shop", "web-1:1:aaaaaa", ["orders"], runs: false, roles: web).StartAsync(CancellationToken.None);
        await Registry("shop", "worker-1:1:bbbbbb", ["orders"], role: "worker", roles: worker).StartAsync(CancellationToken.None);

        Assert.Equal((false, false), (web.Workers, web.Planner));
        Assert.Equal((true, false), (worker.Workers, worker.Planner));
    }

    [PostgresFact]
    public async Task A_switch_that_would_run_a_queue_another_application_runs_leaves_it_off_and_the_health_check_says_why()
    {
        await Registry("billing", "billing-1:1:aaaaaa", ["orders"]).StartAsync(CancellationToken.None);
        var roles = new RoleSwitch();
        var health = new Monitoring.HealthSignals();
        var shop = Registry("shop", "shop-1:1:bbbbbb", ["orders"], runs: false, roles: roles, health: health);
        await shop.StartAsync(CancellationToken.None);

        await SwitchAsync("shop", "", workers: true, planner: true);
        await shop.HeartbeatAsync(CancellationToken.None);

        Assert.Equal((false, false), (roles.Workers, roles.Planner));
        Assert.Contains("billing", health.SwitchRefused);
        Assert.Equal(0, await ScalarAsync<int>($"SELECT cardinality(queues) FROM {Schema}.instances WHERE application = 'shop'"));
    }

    [PostgresFact]
    public async Task Two_applications_that_took_one_name_are_each_told_of_the_other_and_the_monitor_shows_it_until_one_stops()
    {
        var shopHealth = new Monitoring.HealthSignals();
        var stockHealth = new Monitoring.HealthSignals();
        var shop = Registry("Api", "shop-1:1:aaaaaa", ["shop.orders"], health: shopHealth);
        var stock = Registry("Api", "stock-1:1:bbbbbb", ["stock.items"], health: stockHealth);
        await shop.StartAsync(CancellationToken.None);
        await stock.StartAsync(CancellationToken.None);
        var shopBeforeItsHeartbeat = shopHealth.SharedName;

        await shop.HeartbeatAsync(CancellationToken.None);
        var shown = (await MonitorTests.MonitorFor(Store, Schema, _clock).GetInstancesAsync()).GetValueOrThrow();
        var shopSaw = shopHealth.SharedName;
        await stock.StopAsync(CancellationToken.None);
        await shop.HeartbeatAsync(CancellationToken.None);

        Assert.Null(shopBeforeItsHeartbeat);
        Assert.Equal(("Api", "stock-1:1:bbbbbb on stock-1"), shopSaw);
        Assert.Equal(("Api", "shop-1:1:aaaaaa on shop-1"), stockHealth.SharedName);
        Assert.Equal(
            [("shop-1:1:aaaaaa", "stock-1:1:bbbbbb"), ("stock-1:1:bbbbbb", "shop-1:1:aaaaaa")],
            shown.Select(instance => (instance.Id, Assert.Single(instance.SharesNameWith))).ToList());
        Assert.Null(shopHealth.SharedName);
    }

    [PostgresFact]
    public async Task Copies_of_one_application_a_web_instance_and_a_deploy_that_adds_a_queue_share_its_name_without_a_warning()
    {
        var healths = new List<Monitoring.HealthSignals>();
        foreach (var (id, queues, runs) in new[]
                 {
                     ("worker-1:1:aaaaaa", new[] { "orders" }, true),
                     ("worker-2:1:bbbbbb", new[] { "orders" }, true),
                     ("web-1:1:cccccc", new[] { "orders" }, false),
                     ("worker-3:1:dddddd", new[] { "orders", "returns" }, true)
                 })
        {
            var health = new Monitoring.HealthSignals();
            healths.Add(health);
            await Registry("shop", id, queues, runs: runs, health: health).StartAsync(CancellationToken.None);
        }

        var shown = (await MonitorTests.MonitorFor(Store, Schema, _clock).GetInstancesAsync()).GetValueOrThrow();

        Assert.All(healths, health => Assert.Null(health.SharedName));
        Assert.All(shown, instance => Assert.Empty(instance.SharesNameWith));
    }

    [PostgresFact]
    public async Task An_instance_that_shares_its_name_with_another_application_is_degraded_and_told_how_to_fix_it()
    {
        var health = new Monitoring.HealthSignals();
        await Registry("Api", "stock-1:1:bbbbbb", ["stock.items"]).StartAsync(CancellationToken.None);
        await Registry("Api", "shop-1:1:aaaaaa", ["shop.orders"], health: health).StartAsync(CancellationToken.None);
        var check = new SyncLaneHealthCheck(
            Store, health,
            new Monitoring.JobChangeFeed(ConnectionString!, Store.DataSource, Schema, _clock, NullLogger<Monitoring.JobChangeFeed>.Instance, listens: false),
            _clock);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, result.Status);
        Assert.Contains("Instances stock-1:1:bbbbbb on stock-1 also register as application Api", result.Description);
        Assert.Contains("options.Application (SyncLane:Application)", result.Description);
    }

    private Task<int> SwitchAsync(string application, string role, bool workers, bool planner) =>
        ExecuteAsync(
            $"INSERT INTO {Schema}.switches (application, role, run_workers, run_planner, changed_at) " +
            $"VALUES ('{application}', '{role}', {workers}, {planner}, now()) " +
            "ON CONFLICT (application, role) DO UPDATE SET run_workers = excluded.run_workers, run_planner = excluded.run_planner");

    [PostgresFact]
    public async Task A_budget_declared_differently_elsewhere_does_not_stop_the_start_and_both_declarations_are_stored()
    {
        await Registry("shipping", "host-a:1", ["orders"], budget: 10).StartAsync(CancellationToken.None);
        await Registry("billing", "host-b:1", ["invoices"], budget: 5).StartAsync(CancellationToken.None);

        var stored = await ScalarAsync<string>($"SELECT string_agg(budgets::text, ' ' ORDER BY id) FROM {Schema}.instances");
        Assert.Contains("\"limit\": 10", stored);
        Assert.Contains("\"limit\": 5", stored);
    }

    private InstanceRegistry Registry(
        string application, string id, string[] queues, bool runs = true, int? budget = null, string role = "",
        RoleSwitch? roles = null, Monitoring.HealthSignals? health = null)
    {
        var options = new SyncLaneOptions { RunPlanner = runs, RunWorkers = runs, ListenForChanges = runs };
        foreach (var queue in queues)
            options.AddQueue(queue, declared => declared.Handle<ProductHandler>("products"));
        if (budget is { } limit)
            options.AddBudget("api", limit, TimeSpan.FromSeconds(1));

        return new InstanceRegistry(
            options,
            new InstanceStore(Store.DataSource, new InstanceSql(Schema)),
            new InstanceIdentity(id, id.Split(':')[0], application, role, "1.0.0"),
            roles ?? new RoleSwitch(),
            new Monitoring.JobChangeFeed(ConnectionString!, Store.DataSource, Schema, _clock, NullLogger<Monitoring.JobChangeFeed>.Instance, listens: false),
            _clock,
            health ?? new Monitoring.HealthSignals(),
            NullLogger<InstanceRegistry>.Instance);
    }

    private async Task<List<string>> IdsAsync() =>
        (await ScalarAsync<string>($"SELECT coalesce(string_agg(id, ',' ORDER BY id), '') FROM {Schema}.instances"))!
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .ToList();
}
