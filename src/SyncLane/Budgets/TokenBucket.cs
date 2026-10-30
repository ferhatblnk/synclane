namespace SyncLane.Budgets;

/// <summary>
/// The tokens of one budget for one key. They refill continuously at the budget's rate, up to its burst. A request
/// reserves a token; when none is free the count goes below zero and the request waits exactly until its token is due,
/// so waiters queue in order instead of waking together and racing.
/// </summary>
/// <remarks>
/// A pause empties the bucket and stops the refill until it ends, so requests restart at the steady rate rather than all
/// at once. Reservations made before a pause are void: their holders reserve again, behind it.
/// </remarks>
internal sealed class TokenBucket
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxPause = TimeSpan.FromDays(1);

    private readonly Lock _lock = new();
    private readonly double _perSecond;
    private readonly double _burst;

    private double _tokens;
    private DateTimeOffset _refilledAt;
    private long _generation;
    private int _streak;
    private DateTimeOffset _lastThrottle = DateTimeOffset.MinValue;
    private TimeSpan _lastPause;

    public TokenBucket(int limit, TimeSpan per, int burst, DateTimeOffset now)
    {
        _perSecond = limit / per.TotalSeconds;
        _burst = burst;
        _tokens = burst;
        _refilledAt = now;
    }

    /// <summary>Takes a token, now or when it is due, unless that is further away than <paramref name="maxWait"/>.</summary>
    public Reservation Reserve(DateTimeOffset now, TimeSpan maxWait)
    {
        lock (_lock)
        {
            Refill(now);
            var after = _tokens - 1;
            var paused = _refilledAt > now ? _refilledAt - now : TimeSpan.Zero;
            var wait = paused + (after < 0 ? TimeSpan.FromSeconds(-after / _perSecond) : TimeSpan.Zero);

            if (wait > maxWait)
                return new Reservation(false, wait, _generation);

            _tokens = after;
            return new Reservation(true, wait, _generation);
        }
    }

    /// <summary>Whether a reservation still counts: no pause has come since it was made.</summary>
    public bool Holds(Reservation reservation)
    {
        lock (_lock)
            return reservation.Generation == _generation;
    }

    /// <summary>Gives back a token that was reserved but will not be used.</summary>
    public void Return(Reservation reservation)
    {
        lock (_lock)
        {
            if (reservation.Granted && reservation.Generation == _generation)
                _tokens = Math.Min(_burst, _tokens + 1);
        }
    }

    /// <summary>
    /// Pauses the bucket because the other side asked to slow down: for <paramref name="retryAfter"/> when it said how
    /// long, otherwise for a backoff that doubles while throttles keep coming. Returns how long the pause lasts.
    /// </summary>
    public TimeSpan Pause(DateTimeOffset now, TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            // The streak is forgotten after a quiet spell twice as long as the last pause.
            if (now - _lastThrottle > _lastPause * 2)
                _streak = 0;

            _streak++;
            var pause = retryAfter is { } asked ? Clamp(asked) : Backoff();
            _lastThrottle = now;
            _lastPause = pause;

            Refill(now);
            if (now + pause > _refilledAt)
                _refilledAt = now + pause;

            _tokens = 0;
            _generation++;
            return _refilledAt - now;
        }
    }

    private TimeSpan Backoff()
    {
        var oneToken = TimeSpan.FromSeconds(1 / _perSecond);
        var first = oneToken > MinBackoff ? oneToken : MinBackoff;
        var ticks = first.Ticks * Math.Pow(2, Math.Min(_streak - 1, 20));
        return ticks >= MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }

    private static TimeSpan Clamp(TimeSpan asked) =>
        asked < TimeSpan.Zero ? TimeSpan.Zero : asked > MaxPause ? MaxPause : asked;

    private void Refill(DateTimeOffset now)
    {
        if (now <= _refilledAt)
            return;

        _tokens = Math.Min(_burst, _tokens + (now - _refilledAt).TotalSeconds * _perSecond);
        _refilledAt = now;
    }
}

/// <summary>A token taken from a bucket: whether it was granted, how long until it is due, and the pause it belongs to.</summary>
internal readonly record struct Reservation(bool Granted, TimeSpan Wait, long Generation);
