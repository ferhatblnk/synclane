using Npgsql;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Budgets;

/// <summary>The bucket's arithmetic, in the database, with explicit times.</summary>
public class BudgetStoreTests : PostgresTest
{
    private const string Key = "owner-1";
    private static readonly TimeSpan Forever = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private BudgetStore Budgets => new(Store.DataSource, new BudgetSql(Schema));

    [PostgresFact]
    public async Task A_full_bucket_gives_its_burst_at_once_then_spaces_requests_at_the_rate()
    {
        var budget = Budget(limit: 2, burst: 3);

        var waits = await ReserveAsync(budget, Noon, 5);

        Assert.Equal([0, 0, 0, 0.5, 1.0], waits.Select(reservation => reservation.Wait.TotalSeconds));
    }

    [PostgresFact]
    public async Task Tokens_refill_over_time_but_never_above_the_burst()
    {
        var budget = Budget(limit: 2, burst: 3);
        await ReserveAsync(budget, Noon, 3);

        var waits = await ReserveAsync(budget, Noon.AddMinutes(10), 4);

        Assert.Equal([0, 0, 0, 0.5], waits.Select(reservation => reservation.Wait.TotalSeconds));
    }

    [PostgresFact]
    public async Task A_wait_longer_than_allowed_is_refused_and_takes_nothing()
    {
        var budget = Budget(limit: 2, burst: 1);
        await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        var refused = await Budgets.ReserveAsync(budget, Key, Noon, TimeSpan.FromSeconds(0.4), CancellationToken.None);
        var granted = await Budgets.ReserveAsync(budget, Key, Noon.AddSeconds(0.5), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal((false, 0.5), (refused.Granted, refused.Wait.TotalSeconds));
        Assert.Equal((true, 0.0), (granted.Granted, granted.Wait.TotalSeconds));
    }

    [PostgresFact]
    public async Task Each_key_has_a_bucket_of_its_own()
    {
        var budget = Budget(limit: 1, burst: 1);
        await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        var other = await Budgets.ReserveAsync(budget, "owner-2", Noon, Forever, CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, other.Wait);
    }

    [PostgresFact]
    public async Task Instances_taking_tokens_at_the_same_moment_never_take_more_than_the_bucket_holds()
    {
        var budget = Budget(limit: 1, burst: 10);
        await using var other = NpgsqlDataSource.Create(ConnectionString!);
        BudgetStore[] instances = [Budgets, new(other, new BudgetSql(Schema))];

        var reservations = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            instances[index % 2].ReserveAsync(budget, Key, Noon, TimeSpan.Zero, CancellationToken.None)));

        Assert.Equal(10, reservations.Count(reservation => reservation.Granted));
    }

    [PostgresFact]
    public async Task A_pause_voids_earlier_reservations_and_the_bucket_restarts_empty_after_it()
    {
        var budget = Budget(limit: 2, burst: 5);
        var before = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        var pause = await PauseAsync(budget, before, Noon, TimeSpan.FromSeconds(2));
        var after = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        Assert.Equal((true, Noon.AddSeconds(2)), (pause.Paused, pause.Until));
        Assert.NotEqual(before.Generation, await Budgets.GenerationAsync(budget.Name, Key, CancellationToken.None));
        Assert.Equal(after.Generation, await Budgets.GenerationAsync(budget.Name, Key, CancellationToken.None));

        // The first token after the pause is one token's time at the cut rate, 0.7 times 2 a second.
        Assert.Equal(2 + 1 / 1.4, after.Wait.TotalSeconds, precision: 6);
    }

    [PostgresFact]
    public async Task The_pause_doubles_only_while_the_first_request_after_each_pause_is_refused_again()
    {
        var budget = Budget(limit: 10, burst: 1);

        // Three times in a row the first request after the pause is refused: the API is still full.
        var first = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);
        var one = await PauseAsync(budget, first, Noon, retryAfter: null);
        var second = await Budgets.ReserveAsync(budget, Key, one.Until, Forever, CancellationToken.None);
        var two = await PauseAsync(budget, second, one.Until.AddSeconds(0.5), retryAfter: null);
        var third = await Budgets.ReserveAsync(budget, Key, two.Until, Forever, CancellationToken.None);
        var four = await PauseAsync(budget, third, two.Until.AddSeconds(0.5), retryAfter: null);

        // Then three requests go out after the pause and only the third is refused: the rate was a little too high.
        var through = await ReserveAsync(budget, four.Until, 3);
        var again = await PauseAsync(budget, through[2], four.Until.AddSeconds(1), retryAfter: null);

        Assert.Equal(TimeSpan.FromSeconds(1), one.Until - Noon);
        Assert.Equal(TimeSpan.FromSeconds(2), two.Until - one.Until.AddSeconds(0.5));
        Assert.Equal(TimeSpan.FromSeconds(4), four.Until - two.Until.AddSeconds(0.5));
        Assert.Equal(TimeSpan.FromSeconds(1), again.Until - four.Until.AddSeconds(1));
    }

    [PostgresFact]
    public async Task A_fresh_throttle_cuts_the_rate_to_seven_tenths_and_remembers_the_rate_it_cut_from()
    {
        var budget = Budget(limit: 10, burst: 1);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        var pause = await PauseAsync(budget, sent, Noon, retryAfter: null);

        Assert.Equal(7, pause.Rate, precision: 9);
        Assert.Equal((7.0, 10.0), await RatesAsync());
    }

    [PostgresFact]
    public async Task While_the_budget_is_the_bottleneck_its_rate_climbs_fast_to_nine_tenths_of_the_refused_rate_then_slowly()
    {
        var budget = Budget(limit: 10, burst: 1);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);
        var pause = await PauseAsync(budget, sent, Noon, retryAfter: null);

        // Thirteen tokens taken back to back after the pause: each waits for the refill, so each raises the rate 2%.
        await ReserveAsync(budget, pause.Until, 13);
        var (fast, _) = await RatesAsync();
        await ReserveAsync(budget, pause.Until, 1);
        var (slow, _) = await RatesAsync();

        Assert.Equal(7 * Math.Pow(1.02, 13), fast, precision: 9);
        Assert.True(fast >= 9);
        Assert.Equal(fast * 1.0003, slow, precision: 9);
    }

    [PostgresFact]
    public async Task Above_the_refused_rate_it_climbs_a_tenth_of_a_percent_a_token_and_never_above_the_limit()
    {
        var budget = Budget(limit: 10, burst: 1);
        await ExecuteAsync($"INSERT INTO {Schema}.rate_buckets (budget, key, tokens, refilled_at, rate, rate_max, updated_at) VALUES ('shop', '{Key}', 0, '{Noon:O}', 9, 8.9, '{Noon:O}')");

        await ReserveAsync(budget, Noon, 1);
        var (above, _) = await RatesAsync();
        await ExecuteAsync($"UPDATE {Schema}.rate_buckets SET rate = 9.999");
        await ReserveAsync(budget, Noon, 3);
        var (capped, _) = await RatesAsync();

        Assert.Equal(9 * 1.001, above, precision: 9);
        Assert.Equal(10, capped);
    }

    [PostgresFact]
    public async Task An_idle_or_paused_budget_does_not_raise_its_rate()
    {
        var budget = Budget(limit: 10, burst: 5);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);
        var pause = await PauseAsync(budget, sent, Noon, TimeSpan.FromSeconds(30));

        await ReserveAsync(budget, Noon.AddSeconds(1), 3);
        var (paused, _) = await RatesAsync();
        await ReserveAsync(budget, pause.Until.AddMinutes(10), 3);
        var (idle, _) = await RatesAsync();

        Assert.Equal(7, paused, precision: 9);
        Assert.Equal(7, idle, precision: 9);
    }

    [PostgresFact]
    public async Task The_rate_never_drops_below_a_hundredth_of_the_limit()
    {
        var budget = Budget(limit: 10, burst: 1);
        var at = Noon;

        // Sequential: every cut answers a request sent after the previous pause.
        for (var cut = 0; cut < 20; cut++)
        {
            var sent = await Budgets.ReserveAsync(budget, Key, at, Forever, CancellationToken.None);
            at = (await PauseAsync(budget, sent, at, TimeSpan.Zero)).Until;
        }

        Assert.Equal(0.1, (await RatesAsync()).Rate, precision: 9);
    }

    [PostgresFact]
    public async Task A_budget_that_starts_low_begins_at_its_share_of_the_limit_and_a_lowered_limit_caps_a_learned_rate()
    {
        var low = new BudgetDescription("shop", 10, OneSecond, 1, Start: 0.25);
        await ReserveAsync(low, Noon, 1);
        var (start, _) = await RatesAsync();

        var lowered = new BudgetDescription("shop", 2, OneSecond, 1, Start: 0.25);
        var waits = (await ReserveAsync(lowered, Noon.AddMinutes(1), 2)).Select(reservation => reservation.Wait.TotalSeconds);

        Assert.Equal(2.5, start, precision: 9);
        Assert.Equal([0, 0.5], waits);
    }

    [PostgresFact]
    public async Task Throttles_for_requests_sent_before_a_pause_began_do_not_make_it_longer()
    {
        var budget = Budget(limit: 10, burst: 4);
        var inFlight = await ReserveAsync(budget, Noon, 4);

        // Four requests left together and all four came back "slow down", one after the other.
        var pauses = new List<BudgetPause>();
        foreach (var (reservation, index) in inFlight.Select((reservation, index) => (reservation, index)))
        {
            // Sequential: the answers arrive one after the other, and each sees what the one before did.
            pauses.Add(await PauseAsync(budget, reservation, Noon.AddMilliseconds(10 * index), retryAfter: null));
        }

        Assert.Equal([true, false, false, false], pauses.Select(pause => pause.Paused));
        Assert.All(pauses, pause => Assert.Equal(Noon.AddSeconds(1), pause.Until));
        Assert.All(pauses, pause => Assert.Equal(7, pause.Rate, precision: 9));
    }

    [PostgresFact]
    public async Task A_throttle_for_an_earlier_request_still_lengthens_the_pause_when_it_asks_for_longer()
    {
        var budget = Budget(limit: 10, burst: 4);
        var inFlight = await ReserveAsync(budget, Noon, 2);

        await PauseAsync(budget, inFlight[0], Noon, TimeSpan.FromSeconds(1));
        var waiting = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);
        var longer = await PauseAsync(budget, inFlight[1], Noon, TimeSpan.FromSeconds(5));

        Assert.Equal(new BudgetPause(true, Noon.AddSeconds(5), 7), longer);
        Assert.NotEqual(waiting.Generation, await Budgets.GenerationAsync(budget.Name, Key, CancellationToken.None));
    }

    [PostgresFact]
    public async Task A_shorter_retry_after_never_cuts_a_running_pause()
    {
        var budget = Budget(limit: 2, burst: 1);

        var first = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);
        await PauseAsync(budget, first, Noon, TimeSpan.FromSeconds(30));
        var second = await Budgets.ReserveAsync(budget, Key, Noon.AddSeconds(1), Forever, CancellationToken.None);
        var shorter = await PauseAsync(budget, second, Noon.AddSeconds(1), TimeSpan.FromSeconds(2));

        Assert.Equal(Noon.AddSeconds(30), shorter.Until);
    }

    [PostgresFact]
    public async Task A_bucket_whose_row_was_deleted_starts_again_full()
    {
        var budget = Budget(limit: 1, burst: 1);
        await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        await ExecuteAsync($"DELETE FROM {Schema}.rate_buckets");
        var again = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, again.Wait);
    }

    [PostgresFact]
    public async Task A_reading_lowers_the_bucket_to_what_the_api_has_left_and_never_raises_it()
    {
        var budget = Budget(limit: 2, burst: 10);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        Assert.Null(await ObserveAsync(budget, sent.Sequence, remaining: 100, resetAt: null, Noon));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 0, 0.5], (await ReserveAsync(budget, Noon, 10)).Select(reservation => reservation.Wait.TotalSeconds));

        var lowered = await Budgets.ReserveAsync(budget, "owner-2", Noon, Forever, CancellationToken.None);
        await ObserveAsync(budget, lowered.Sequence, remaining: 3, resetAt: null, Noon, key: "owner-2");
        var waits = new List<double>();

        // Sequential: each reservation queues behind the one before it.
        for (var index = 0; index < 4; index++)
            waits.Add((await Budgets.ReserveAsync(budget, "owner-2", Noon, Forever, CancellationToken.None)).Wait.TotalSeconds);

        Assert.Equal([0, 0, 0, 0.5], waits);
    }

    [PostgresFact]
    public async Task Requests_that_took_a_token_after_the_answered_one_are_subtracted_from_what_the_api_has_left()
    {
        var budget = Budget(limit: 2, burst: 10);
        var inFlight = await ReserveAsync(budget, Noon, 5);

        // The API answered the first request with 6 left. Four more requests are on their way and not counted yet.
        await ObserveAsync(budget, inFlight[0].Sequence, remaining: 6, resetAt: null, Noon);

        Assert.Equal([0, 0, 0.5], (await ReserveAsync(budget, Noon, 3)).Select(reservation => reservation.Wait.TotalSeconds));
    }

    [PostgresFact]
    public async Task With_nothing_left_and_a_reset_ahead_the_bucket_pauses_until_the_reset_and_restarts_empty()
    {
        var budget = Budget(limit: 2, burst: 5);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        var paused = await ObserveAsync(budget, sent.Sequence, remaining: 0, resetAt: Noon.AddSeconds(10), Noon);
        var after = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        Assert.Equal(Noon.AddSeconds(10), paused);
        Assert.NotEqual(sent.Generation, after.Generation);
        Assert.Equal(10.5, after.Wait.TotalSeconds);
    }

    [PostgresFact]
    public async Task With_nothing_left_and_no_reset_the_bucket_empties_and_waiters_queue_again()
    {
        var budget = Budget(limit: 2, burst: 5);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        var paused = await ObserveAsync(budget, sent.Sequence, remaining: 0, resetAt: null, Noon);
        var after = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        Assert.Null(paused);
        Assert.NotEqual(sent.Generation, after.Generation);
        Assert.Equal(0.5, after.Wait.TotalSeconds);
    }

    [PostgresFact]
    public async Task A_reset_that_has_passed_does_not_pause()
    {
        var budget = Budget(limit: 2, burst: 5);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        Assert.Null(await ObserveAsync(budget, sent.Sequence, remaining: 0, resetAt: Noon.AddSeconds(-1), Noon));
    }

    [PostgresFact]
    public async Task A_pause_for_a_spent_quota_does_not_lengthen_the_backoff_of_a_later_throttle()
    {
        var budget = Budget(limit: 2, burst: 1);
        var sent = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);
        await ObserveAsync(budget, sent.Sequence, remaining: 0, resetAt: Noon.AddSeconds(2), Noon);

        var later = await Budgets.ReserveAsync(budget, Key, Noon.AddSeconds(3), Forever, CancellationToken.None);
        var throttle = await PauseAsync(budget, later, Noon.AddSeconds(3), retryAfter: null);

        Assert.Equal(Noon.AddSeconds(4), throttle.Until);
    }

    private static BudgetDescription Budget(int limit, int burst) => new("shop", limit, OneSecond, burst);

    private async Task<List<Reservation>> ReserveAsync(BudgetDescription budget, DateTimeOffset now, int count)
    {
        var reservations = new List<Reservation>();

        // Sequential: each reservation queues behind the one before it.
        for (var index = 0; index < count; index++)
            reservations.Add(await Budgets.ReserveAsync(budget, Key, now, Forever, CancellationToken.None));

        return reservations;
    }

    private Task<DateTimeOffset?> ObserveAsync(
        BudgetDescription budget, long sequence, double remaining, DateTimeOffset? resetAt, DateTimeOffset now, string key = Key) =>
        Budgets.ObserveAsync(budget, key, sequence, remaining, resetAt, now, CancellationToken.None);

    private Task<BudgetPause> PauseAsync(BudgetDescription budget, Reservation sent, DateTimeOffset now, TimeSpan? retryAfter) =>
        Budgets.PauseAsync(budget, Key, sent.Generation, sent.Sequence, now, retryAfter, OneSecond, FiveMinutes, CancellationToken.None);

    /// <summary>The bucket's learned rate and the rate that last caused a throttle, as stored.</summary>
    private async Task<(double Rate, double? RateMax)> RatesAsync()
    {
        await using var source = NpgsqlDataSource.Create(ConnectionString!);
        await using var command = source.CreateCommand($"SELECT rate, rate_max FROM {Schema}.rate_buckets WHERE key = '{Key}'");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetDouble(0), reader.IsDBNull(1) ? null : reader.GetDouble(1));
    }
}
