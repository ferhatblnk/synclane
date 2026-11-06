namespace SyncLane.Tests.Support;

/// <summary>
/// Tests whose outcome depends on how fast requests go, against rate limits and windows on the real clock, run alone,
/// after every other test. Beside the rest of the suite on a busy CI runner they failed both ways: requests that had
/// waited through a stall arrived together and were refused although no budget was exceeded, and a run slowed down so
/// much that the API was never pushed past its limit when it should have been. They measure SyncLane, not how busy the
/// machine is. Tests that only wait for a job to finish need no place here: a slow machine only makes them later.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClockCollection
{
    public const string Name = "Wall clock";
}
