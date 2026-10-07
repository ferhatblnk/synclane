namespace SyncLane;

/// <summary>Who a job works for: one connected account, tenant or store.</summary>
/// <param name="Key">Stable identifier of the owner.</param>
/// <param name="Lane">
/// The lane the owner's jobs run in. Owners that share a rate limit should share a lane.
/// When it is left out, the owner has a lane of its own.
/// </param>
public sealed record JobOwner(string Key, string? Lane = null)
{
    internal string LaneOrKey => string.IsNullOrEmpty(Lane) ? Key : Lane;
}
