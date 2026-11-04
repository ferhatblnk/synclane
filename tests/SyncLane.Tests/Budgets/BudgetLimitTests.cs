using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Budgets;

/// <summary>
/// The ceiling of a budget: the limit the API reports, or else the declared one, and never above a temporary limit an
/// operator sets.
/// </summary>
public class BudgetLimitTests : PostgresTest
{
    private const string Key = "owner-1";
    private static readonly TimeSpan Forever = TimeSpan.FromDays(1);
    private readonly ManualClock _clock = new(Noon);

    private BudgetStore Budgets => new(Store.DataSource, new BudgetSql(Schema));

    [PostgresFact]
    public async Task A_limit_the_api_reports_takes_the_place_of_the_declared_one_up_and_down()
    {
        var budget = Budget(limit: 10);
        await ExecuteAsync(
            $"INSERT INTO {Schema}.rate_buckets (budget, key, tokens, refilled_at, rate, updated_at) VALUES ('shop', '{Key}', 0, '{Noon:O}', 9.999, '{Noon:O}')");

        await Budgets.ReportLimitAsync("shop", Key, 12, Noon, CancellationToken.None);
        await ReserveAsync(budget, Noon, 3);
        var raised = await RateAsync();
        await Budgets.ReportLimitAsync("shop", Key, 2, Noon, CancellationToken.None);
        var lowered = await ReserveAsync(budget, Noon.AddMinutes(1), 3);

        // The declared 10 would have capped the climb at 10.
        Assert.Equal(9.999 * Math.Pow(1.02, 3), raised, precision: 9);
        Assert.Equal([0, 0.5, 1.0], lowered);
    }

    [PostgresFact]
    public async Task A_report_that_repeats_the_known_limit_writes_nothing()
    {
        await ReserveAsync(Budget(limit: 10), Noon, 1);

        var first = await Budgets.ReportLimitAsync("shop", Key, 2, Noon, CancellationToken.None);
        var same = await Budgets.ReportLimitAsync("shop", Key, 2, Noon.AddSeconds(1), CancellationToken.None);
        var changed = await Budgets.ReportLimitAsync("shop", Key, 3, Noon.AddSeconds(2), CancellationToken.None);

        Assert.Equal((true, false, true), (first, same, changed));
    }

    [PostgresFact]
    public async Task An_operator_limit_holds_below_the_reported_limit_and_the_rate_climbs_again_after_it_ends()
    {
        var budget = Budget(limit: 10);
        await ReserveAsync(budget, Noon, 1);
        await Budgets.ReportLimitAsync("shop", Key, 20, Noon, CancellationToken.None);
        await LimitAsync("", 4, Noon.AddHours(1));

        var limited = await ReserveAsync(budget, Noon.AddMinutes(1), 3);
        var during = await RateAsync();
        await ReserveAsync(budget, Noon.AddHours(2), 3);
        var after = await RateAsync();

        Assert.Equal([0, 0.25, 0.5], limited);
        Assert.Equal(4, during, precision: 9);
        Assert.Equal(4 * 1.02 * 1.02, after, precision: 9);
    }

    [PostgresFact]
    public async Task The_lower_of_a_limit_on_every_key_and_one_on_the_key_counts_and_other_keys_keep_their_own()
    {
        var budget = Budget(limit: 10);
        await LimitAsync("", 5, null);
        await LimitAsync(Key, 2, null);

        var own = await ReserveAsync(budget, Noon, 2);
        var other = await ReserveAsync(budget, Noon, 2, "owner-2");
        var unlimited = await ReserveAsync(new BudgetDescription("other", 10, TimeSpan.FromSeconds(1), 1), Noon, 2);

        Assert.Equal([0, 0.5], own);
        Assert.Equal([0, 0.2], other);
        Assert.Equal([0, 0.1], unlimited);
    }

    [PostgresFact]
    public async Task An_operator_sets_lists_and_clears_a_limit_the_budget_list_shows_it_and_each_step_is_logged()
    {
        var monitor = MonitorTests.MonitorFor(Store, Schema, _clock);
        await ReserveAsync(Budget(limit: 10), Noon, 1);
        await Budgets.ReportLimitAsync("shop", Key, 8, Noon, CancellationToken.None);

        var set = await monitor.LimitBudgetAsync("shop", null, 30, TimeSpan.FromMinutes(1), "the API answers slowly", by: "ayse", until: Noon.AddHours(1));
        var limits = (await monitor.GetBudgetLimitsAsync()).GetValueOrThrow();
        var budgets = await monitor.GetBudgetsAsync(new BudgetQuery());
        var cleared = await monitor.ClearBudgetLimitAsync("shop", by: "mehmet");
        var again = await monitor.ClearBudgetLimitAsync("shop");
        var actions = await monitor.GetActionsAsync(new ActionQuery());

        Assert.Equal(1, set.GetValueOrThrow());
        var limit = Assert.Single(limits);
        Assert.Equal(("shop", (string?)null, 0.5, (DateTimeOffset?)Noon.AddHours(1), "ayse", "the API answers slowly"),
            (limit.Budget, limit.Key, limit.Rate, limit.Until, limit.By, limit.Reason));
        var state = Assert.Single(budgets.Items);
        Assert.Equal(((double?)8, (double?)0.5), (state.ReportedRate, state.LimitedTo));
        Assert.Equal((1, 0), (cleared.GetValueOrThrow(), again.GetValueOrThrow()));
        Assert.Equal([("clear budget limit", "mehmet"), ("limit budget", "ayse")], actions.Items.Select(action => (action.Action, action.By)));
        Assert.Empty((await monitor.GetBudgetLimitsAsync()).GetValueOrThrow());
    }

    [PostgresFact]
    public async Task A_limit_below_one_an_empty_period_or_an_end_that_has_passed_is_refused_and_changes_nothing()
    {
        var monitor = MonitorTests.MonitorFor(Store, Schema, _clock);

        SyncResult<int>[] refused =
        [
            await monitor.LimitBudgetAsync("shop", null, 0, TimeSpan.FromMinutes(1), "slow"),
            await monitor.LimitBudgetAsync("shop", null, 5, TimeSpan.Zero, "slow"),
            await monitor.LimitBudgetAsync("shop", null, 5, TimeSpan.FromMinutes(1), "slow", until: Noon)
        ];

        Assert.All(refused, result => Assert.Equal((SyncErrorCodes.BudgetLimitInvalid, SyncErrorKind.Validation), (result.Error!.Code, result.Error.Kind)));
        Assert.Empty((await monitor.GetBudgetLimitsAsync()).GetValueOrThrow());
        Assert.Empty((await monitor.GetActionsAsync(new ActionQuery())).Items);
    }

    [PostgresFact]
    public async Task An_ended_limit_is_not_listed_and_clean_up_deletes_it()
    {
        var monitor = MonitorTests.MonitorFor(Store, Schema, _clock);
        await monitor.LimitBudgetAsync("shop", null, 5, TimeSpan.FromSeconds(1), "slow", until: Noon.AddMinutes(5));
        await monitor.LimitBudgetAsync("shop", "owner-2", 5, TimeSpan.FromSeconds(1), "slow");

        _clock.Advance(TimeSpan.FromMinutes(10));
        var listed = (await monitor.GetBudgetLimitsAsync()).GetValueOrThrow();
        await Store.DeleteOldStatsAsync(Noon.AddDays(-400), _clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal("owner-2", Assert.Single(listed).Key);
        Assert.Equal(1, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.budget_limits"));
    }

    private static BudgetDescription Budget(int limit) => new("shop", limit, TimeSpan.FromSeconds(1), 1);

    private Task<int> LimitAsync(string key, double rate, DateTimeOffset? until) => ExecuteAsync(
        $"INSERT INTO {Schema}.budget_limits (budget, key, rate, until, set_by, reason, set_at) " +
        $"VALUES ('shop', '{key}', {rate}, {(until is { } end ? $"'{end:O}'" : "NULL")}, 'test', 'test', '{Noon:O}')");

    private async Task<List<double>> ReserveAsync(BudgetDescription budget, DateTimeOffset now, int count, string key = Key)
    {
        var waits = new List<double>();

        // Sequential: each reservation queues behind the one before it.
        for (var index = 0; index < count; index++)
            waits.Add((await Budgets.ReserveAsync(budget, key, now, Forever, CancellationToken.None)).Wait.TotalSeconds);

        return waits;
    }

    private Task<double> RateAsync() => ScalarAsync<double>($"SELECT rate FROM {Schema}.rate_buckets WHERE budget = 'shop' AND key = '{Key}'");
}
