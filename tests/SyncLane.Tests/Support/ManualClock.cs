namespace SyncLane.Tests.Support;

/// <summary>A clock the test moves by hand. Timers still run on real time.</summary>
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset now) => _now = now;

    public void Advance(TimeSpan by) => _now += by;
}
