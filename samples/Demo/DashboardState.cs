using SyncLane;

namespace Demo;

/// <summary>Everything the dashboard page needs for one refresh.</summary>
internal sealed record DashboardState(IReadOnlyList<AccountState> Accounts, IReadOnlyList<Job> Jobs);
