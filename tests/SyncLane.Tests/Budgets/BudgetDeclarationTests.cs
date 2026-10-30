using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncLane.Storage;
using SyncLane.Telemetry;

namespace SyncLane.Tests.Budgets;

/// <summary>What is decided before a budget touches the database.</summary>
public class BudgetDeclarationTests
{
    [Fact]
    public void A_budget_is_declared_once_with_settings_that_can_work()
    {
        var twice = Assert.Throws<SyncLaneException>(() => new SyncLaneOptions()
            .AddBudget("shop", 1, TimeSpan.FromSeconds(1))
            .AddBudget("shop", 2, TimeSpan.FromSeconds(1)));

        Assert.Equal(SyncErrorCodes.BudgetDeclaredTwice, twice.Error.Code);
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("shop", 0, TimeSpan.FromSeconds(1))));
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("shop", 1, TimeSpan.Zero)));
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("shop", 1, TimeSpan.FromSeconds(1), burst: 0)));
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("two words", 1, TimeSpan.FromSeconds(1))));
        Assert.Equal(SyncErrorCodes.IntervalNotPositive, Invalid(options => options.MaxBudgetWait = TimeSpan.Zero));
    }

    [Fact]
    public async Task A_budget_that_was_never_declared_is_a_configuration_error()
    {
        var options = new SyncLaneOptions { ConnectionString = "Host=unused" };
        await using var unused = NpgsqlDataSource.Create(options.ConnectionString);
        var budgets = new RateBudgets(options, new BudgetStore(unused, new BudgetSql("synclane")), TimeProvider.System, new SyncLaneMetrics(), NullLogger<RateBudgets>.Instance);

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => budgets.WaitAsync("shop", "owner-1").AsTask());

        Assert.Equal(SyncErrorCodes.BudgetNotDeclared, thrown.Error.Code);
    }

    [Fact]
    public void A_permit_never_shows_its_key()
    {
        var permit = new BudgetPermit("shop", "customer-42", 0);

        Assert.DoesNotContain("customer-42", permit.ToString());
    }

    private static string Invalid(Action<SyncLaneOptions> configure)
    {
        var options = new SyncLaneOptions { ConnectionString = "Host=unused" };
        configure(options);
        return Assert.Throws<SyncLaneException>(options.Validate).Error.Code;
    }
}
