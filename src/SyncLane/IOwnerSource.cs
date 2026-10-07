namespace SyncLane;

/// <summary>Tells the planner who to plan for.</summary>
public interface IOwnerSource
{
    /// <summary>Returns the owners that should currently have jobs in <paramref name="queue"/>.</summary>
    Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken);
}
