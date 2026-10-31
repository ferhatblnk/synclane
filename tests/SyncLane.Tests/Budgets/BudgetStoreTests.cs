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

        var pause = await PauseAsync(budget, before.Generation, Noon, TimeSpan.FromSeconds(2));
        var after = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);

        Assert.Equal(new BudgetPause(true, Noon.AddSeconds(2)), pause);
        Assert.NotEqual(before.Generation, await Budgets.GenerationAsync(budget.Name, Key, CancellationToken.None));
        Assert.Equal(after.Generation, await Budgets.GenerationAsync(budget.Name, Key, CancellationToken.None));
        Assert.Equal(2.5, after.Wait.TotalSeconds);
    }

    [PostgresFact]
    public async Task Without_retry_after_the_pause_doubles_while_throttles_continue_and_is_forgotten_after_a_quiet_spell()
    {
        var budget = Budget(limit: 2, burst: 1);

        var pauses = new List<double>();
        foreach (var at in new[] { Noon, Noon.AddSeconds(1.5), Noon.AddSeconds(4), Noon.AddMinutes(5) })
        {
            // Sequential: each throttle answers a request sent after the previous pause, which is what makes it count.
            var generation = await Budgets.GenerationAsync(budget.Name, Key, CancellationToken.None) ?? 0;
            var pause = await PauseAsync(budget, generation, at, retryAfter: null);
            pauses.Add((pause.Until - at).TotalSeconds);
        }

        Assert.Equal([1.0, 2.0, 4.0, 1.0], pauses);
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
            pauses.Add(await PauseAsync(budget, reservation.Generation, Noon.AddMilliseconds(10 * index), retryAfter: null));
        }

        Assert.Equal([true, false, false, false], pauses.Select(pause => pause.Paused));
        Assert.All(pauses, pause => Assert.Equal(Noon.AddSeconds(1), pause.Until));
    }

    [PostgresFact]
    public async Task A_throttle_for_an_earlier_request_still_lengthens_the_pause_when_it_asks_for_longer()
    {
        var budget = Budget(limit: 10, burst: 4);
        var inFlight = await ReserveAsync(budget, Noon, 2);

        await PauseAsync(budget, inFlight[0].Generation, Noon, TimeSpan.FromSeconds(1));
        var waiting = await Budgets.ReserveAsync(budget, Key, Noon, Forever, CancellationToken.None);
        var longer = await PauseAsync(budget, inFlight[1].Generation, Noon, TimeSpan.FromSeconds(5));

        Assert.Equal(new BudgetPause(true, Noon.AddSeconds(5)), longer);
        Assert.NotEqual(waiting.Generation, await Budgets.GenerationAsync(budget.Name, Key, CancellationToken.None));
    }

    [PostgresFact]
    public async Task A_shorter_retry_after_never_cuts_a_running_pause()
    {
        var budget = Budget(limit: 2, burst: 1);

        await PauseAsync(budget, 0, Noon, TimeSpan.FromSeconds(30));
        var shorter = await PauseAsync(budget, 1, Noon.AddSeconds(1), TimeSpan.FromSeconds(2));

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
        var throttle = await PauseAsync(budget, later.Generation, Noon.AddSeconds(3), retryAfter: null);

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

    private Task<BudgetPause> PauseAsync(BudgetDescription budget, long generation, DateTimeOffset now, TimeSpan? retryAfter) =>
        Budgets.PauseAsync(budget, Key, generation, now, retryAfter, OneSecond, FiveMinutes, CancellationToken.None);
}
