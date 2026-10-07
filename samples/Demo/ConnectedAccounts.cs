using SyncLane;

namespace Demo;

/// <summary>
/// The accounts the demo syncs. In a real application this is a query on your own table of connections.
/// Two of the accounts belong to the same seller and share its rate limit, so they share a lane.
/// </summary>
internal sealed class ConnectedAccounts : IOwnerSource
{
    private const string SharedLane = "acme";

    private readonly Lock _gate = new();
    private readonly List<JobOwner> _accounts =
    [
        new("acme-us", SharedLane),
        new("acme-eu", SharedLane),
        new("globex"),
        new("initech")
    ];

    private int _newcomers;

    public IReadOnlyList<JobOwner> All
    {
        get
        {
            lock (_gate)
                return [.. _accounts];
        }
    }

    public JobOwner Connect()
    {
        lock (_gate)
        {
            var account = new JobOwner($"newcomer-{++_newcomers}");
            _accounts.Add(account);
            return account;
        }
    }

    public Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
        Task.FromResult(All);
}
