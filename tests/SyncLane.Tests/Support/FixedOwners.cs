namespace SyncLane.Tests.Support;

/// <summary>An owner source with a fixed list.</summary>
public sealed class FixedOwners(IReadOnlyList<JobOwner> owners) : IOwnerSource
{
    public Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
        Task.FromResult(owners);
}
