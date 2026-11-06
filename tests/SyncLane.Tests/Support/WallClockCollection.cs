namespace SyncLane.Tests.Support;

/// <summary>
/// Tests that measure real time against a rate limit run alone, after every other test. Run beside the rest of the suite
/// on a busy CI runner, the process stalled for a moment and the requests that had waited arrived together, so the fake
/// API refused a few although no budget was exceeded. They measure SyncLane, not how busy the machine is.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClockCollection
{
    public const string Name = "Wall clock";
}
