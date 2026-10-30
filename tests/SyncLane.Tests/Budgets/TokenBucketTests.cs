using SyncLane.Budgets;

namespace SyncLane.Tests.Budgets;

public class TokenBucketTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Forever = TimeSpan.FromDays(1);

    [Fact]
    public void A_full_bucket_gives_its_burst_at_once_then_spaces_requests_at_the_rate()
    {
        var bucket = new TokenBucket(limit: 2, per: TimeSpan.FromSeconds(1), burst: 3, Noon);

        var waits = Enumerable.Range(0, 5).Select(_ => bucket.Reserve(Noon, Forever).Wait.TotalSeconds).ToArray();

        Assert.Equal([0, 0, 0, 0.5, 1.0], waits);
    }

    [Fact]
    public void Tokens_refill_over_time_but_never_above_the_burst()
    {
        var bucket = new TokenBucket(limit: 2, per: TimeSpan.FromSeconds(1), burst: 3, Noon);
        _ = Enumerable.Range(0, 3).Select(_ => bucket.Reserve(Noon, Forever)).ToArray();

        var later = Noon.AddMinutes(10);
        var waits = Enumerable.Range(0, 4).Select(_ => bucket.Reserve(later, Forever).Wait.TotalSeconds).ToArray();

        Assert.Equal([0, 0, 0, 0.5], waits);
    }

    [Fact]
    public void A_wait_longer_than_allowed_is_refused_and_takes_nothing()
    {
        var bucket = new TokenBucket(limit: 2, per: TimeSpan.FromSeconds(1), burst: 1, Noon);
        bucket.Reserve(Noon, Forever);

        var refused = bucket.Reserve(Noon, TimeSpan.FromSeconds(0.4));
        var granted = bucket.Reserve(Noon.AddSeconds(0.5), TimeSpan.Zero);

        Assert.Equal((false, 0.5), (refused.Granted, refused.Wait.TotalSeconds));
        Assert.Equal((true, 0.0), (granted.Granted, granted.Wait.TotalSeconds));
    }

    [Fact]
    public void A_pause_voids_earlier_reservations_and_the_bucket_restarts_empty_after_it()
    {
        var bucket = new TokenBucket(limit: 2, per: TimeSpan.FromSeconds(1), burst: 5, Noon);
        var before = bucket.Reserve(Noon, Forever);

        var pause = bucket.Pause(Noon, retryAfter: TimeSpan.FromSeconds(2));
        var after = bucket.Reserve(Noon, Forever);

        Assert.Equal(TimeSpan.FromSeconds(2), pause);
        Assert.False(bucket.Holds(before));
        Assert.True(bucket.Holds(after));
        Assert.Equal(2.5, after.Wait.TotalSeconds);
    }

    [Fact]
    public void Without_retry_after_the_pause_doubles_while_throttles_continue_and_is_forgotten_after_a_quiet_spell()
    {
        var bucket = new TokenBucket(limit: 2, per: TimeSpan.FromSeconds(1), burst: 1, Noon);

        var first = bucket.Pause(Noon, null);
        var second = bucket.Pause(Noon.AddSeconds(1.5), null);
        var third = bucket.Pause(Noon.AddSeconds(4), null);
        var afterQuiet = bucket.Pause(Noon.AddMinutes(5), null);

        Assert.Equal([1.0, 2.0, 4.0, 1.0], new[] { first, second, third, afterQuiet }.Select(pause => pause.TotalSeconds));
    }

    [Fact]
    public void The_first_pause_of_a_slow_budget_lasts_one_token_and_no_pause_lasts_longer_than_five_minutes()
    {
        var slow = new TokenBucket(limit: 1, per: TimeSpan.FromMinutes(1), burst: 1, Noon);
        var capped = new TokenBucket(limit: 1, per: TimeSpan.FromHours(1), burst: 1, Noon);

        Assert.Equal(TimeSpan.FromMinutes(1), slow.Pause(Noon, null));
        Assert.Equal(TimeSpan.FromMinutes(5), capped.Pause(Noon, null));
    }

    [Fact]
    public void A_shorter_retry_after_never_cuts_a_running_pause()
    {
        var bucket = new TokenBucket(limit: 2, per: TimeSpan.FromSeconds(1), burst: 1, Noon);

        bucket.Pause(Noon, TimeSpan.FromSeconds(30));
        var shorter = bucket.Pause(Noon.AddSeconds(1), TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(29), shorter);
    }

    [Fact]
    public void A_returned_token_can_be_taken_again_but_not_after_a_pause()
    {
        var bucket = new TokenBucket(limit: 1, per: TimeSpan.FromMinutes(1), burst: 1, Noon);
        var taken = bucket.Reserve(Noon, Forever);

        bucket.Return(taken);
        var again = bucket.Reserve(Noon, Forever);
        bucket.Pause(Noon, TimeSpan.Zero);
        bucket.Return(again);

        Assert.Equal(TimeSpan.Zero, again.Wait);
        Assert.Equal(TimeSpan.FromMinutes(1), bucket.Reserve(Noon, Forever).Wait);
    }
}
