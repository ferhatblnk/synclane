using Microsoft.Extensions.Hosting.Internal;
using SyncLane.Instances;
using SyncLane.Storage;

namespace SyncLane.Tests;

public class InstanceRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InstanceIdentity Me = new("me:1", "me", "shipping", "", "1.0.0");

    [Fact]
    public void Only_queues_another_application_runs_conflict_and_each_is_reported_once()
    {
        InstanceRow[] live =
        [
            Row("copy:1", "shipping", ["orders"]),
            Row("billing:1", "billing", ["orders", "invoices"]),
            Row("billing:2", "billing", ["orders"]),
            Row("returns:1", "returns", ["returns"])
        ];

        var conflicts = InstanceRules.Conflicts(Me, ["orders", "labels", "invoices"], live, Now);

        Assert.Equal(2, conflicts.Count);
        Assert.StartsWith("Queue 'orders' is already run by application 'billing' (instance 'billing:1'", conflicts[0].Message);
        Assert.StartsWith("Queue 'invoices'", conflicts[1].Message);
    }

    [Fact]
    public void An_instance_with_neither_planner_nor_workers_runs_no_queue()
    {
        var options = new SyncLaneOptions { RunPlanner = false, RunWorkers = false }
            .AddQueue("orders", queue => queue.Handle<Support.ProductHandler>("products"));

        Assert.Empty(InstanceRules.Runs(options));
        Assert.Equal(["orders"], InstanceRules.Runs(new SyncLaneOptions().AddQueue("orders", queue => queue.Handle<Support.ProductHandler>("products"))));
    }

    [Fact]
    public void A_budget_with_another_limit_period_or_burst_elsewhere_is_a_disagreement()
    {
        BudgetDescription[] mine = [new("api", 10, TimeSpan.FromSeconds(1), 1), new("search", 2, TimeSpan.FromSeconds(1), 1)];
        InstanceRow[] live =
        [
            Row("same:1", "billing", [], new BudgetDescription("api", 10, TimeSpan.FromSeconds(1), 1, Start: 0.5)),
            Row("limit:1", "billing", [], new BudgetDescription("api", 5, TimeSpan.FromSeconds(1), 1)),
            Row("burst:1", "billing", [], new BudgetDescription("search", 2, TimeSpan.FromSeconds(1), 4)),
            Row("other:1", "billing", [], new BudgetDescription("geo", 1, TimeSpan.FromSeconds(1), 1))
        ];

        var disagreements = InstanceRules.Disagreements(mine, live).Select(found => (found.Mine.Name, found.Instance.Id)).ToList();

        Assert.Equal([("api", "limit:1"), ("search", "burst:1")], disagreements);
    }

    [Fact]
    public void Budgets_are_stored_as_json_and_read_back_the_same()
    {
        BudgetDescription[] budgets = [new("api", 40, TimeSpan.FromMinutes(1), 2), new("search", 1, TimeSpan.FromSeconds(0.5), 1)];

        var read = InstanceStore.ReadBudgets(InstanceStore.WriteBudgets(budgets));

        Assert.Equal(budgets, read);
    }

    [Fact]
    public void The_application_name_comes_from_the_options_then_the_host_and_must_be_usable()
    {
        var host = new HostingEnvironment { ApplicationName = "Shipping.Api" };

        var fromHost = InstanceIdentity.Create(new SyncLaneOptions(), host);
        var fromOptions = InstanceIdentity.Create(new SyncLaneOptions { Application = "shipping" }, host);
        var unusable = Assert.Throws<SyncLaneException>(() => InstanceIdentity.Create(new SyncLaneOptions(), new HostingEnvironment { ApplicationName = "My Shipping App" }));

        Assert.Equal(("Shipping.Api", "shipping"), (fromHost.Application, fromOptions.Application));
        Assert.Matches($"^{System.Text.RegularExpressions.Regex.Escape($"{Environment.MachineName}:{Environment.ProcessId}:")}[0-9a-f]{{6}}$", fromHost.Id);
        Assert.DoesNotContain('+', fromHost.Version);
        Assert.Equal(SyncErrorCodes.ApplicationNameInvalid, unusable.Error.Code);
    }

    [Fact]
    public void Two_processes_on_one_host_with_one_process_id_get_different_ids()
    {
        var first = InstanceIdentity.Create(new SyncLaneOptions { Application = "shipping" }, null);
        var second = InstanceIdentity.Create(new SyncLaneOptions { Application = "shipping" }, null);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Host, second.Host);
    }

    [Fact]
    public void The_role_comes_from_the_options_is_empty_without_them_and_must_be_usable()
    {
        var worker = InstanceIdentity.Create(new SyncLaneOptions { Application = "shipping", Role = "worker" }, null);
        var plain = InstanceIdentity.Create(new SyncLaneOptions { Application = "shipping" }, null);
        var options = new SyncLaneOptions { ConnectionString = "Host=localhost", Role = "the worker" };

        var unusable = Assert.Throws<SyncLaneException>(options.Validate);

        Assert.Equal(("worker", ""), (worker.Role, plain.Role));
        Assert.Equal(SyncErrorCodes.RoleNameInvalid, unusable.Error.Code);
    }

    private static InstanceRow Row(string id, string application, string[] queues, params BudgetDescription[] budgets) =>
        new(id, application, id.Split(':')[0], "1.0.0", queues, budgets, Now.AddMinutes(-5), Now.AddSeconds(-10));
}
