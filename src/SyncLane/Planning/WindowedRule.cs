namespace SyncLane.Planning;

/// <summary>History cut into slices back to the horizon, then one window per period for what came after.</summary>
internal sealed record WindowedRule(TimeSpan Horizon, TimeSpan Slice, TimeSpan? Every, TimeSpan Overlap, TimeSpan Offset)
    : PlanRule(Every, Offset)
{
    public override void Validate(string queue, string type)
    {
        base.Validate(queue, type);
        Require(Horizon >= TimeSpan.Zero, queue, type, "horizon must not be negative");
        Require(Slice > TimeSpan.Zero, queue, type, "slice must be positive");
        Require(Overlap >= TimeSpan.Zero, queue, type, "overlap must not be negative");
    }
}
