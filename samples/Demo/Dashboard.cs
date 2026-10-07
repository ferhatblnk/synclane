using SyncLane;

namespace Demo;

/// <summary>
/// The page of the demo and the three endpoints behind it. SyncLane ships no dashboard of its own:
/// this is what building one takes, and the table can always be read with plain SQL instead.
/// </summary>
internal static class Dashboard
{
    public const string Queue = "shops";

    private const string HtmlContentType = "text/html; charset=utf-8";

    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content(DashboardPage.Html, HtmlContentType));
        app.MapGet("/api/state", GetStateAsync);
        app.MapPost("/api/accounts", ConnectAsync);
        app.MapPost("/api/requeue", (ISyncLane lane, CancellationToken cancellationToken) => lane.RequeueAsync(Queue, null, cancellationToken));
    }

    private static async Task<DashboardState> GetStateAsync(
        ISyncLane lane, ConnectedAccounts accounts, DemoData data, CancellationToken cancellationToken)
    {
        var jobs = await lane.GetJobsAsync(new JobQuery { Queue = Queue, Limit = JobQuery.MaxLimit }, cancellationToken);
        var orders = await data.CountOrdersAsync(cancellationToken);
        var products = await data.CountProductsAsync(cancellationToken);
        var firstLoads = jobs.Where(job => job.Mode == JobMode.Initial).ToLookup(job => job.Owner);

        var states = accounts.All
            .Select(account => new AccountState(
                account.Key,
                account.Lane ?? account.Key,
                orders.GetValueOrDefault(account.Key),
                products.GetValueOrDefault(account.Key),
                firstLoads[account.Key].Count(),
                firstLoads[account.Key].Count(job => job.Status == JobStatus.Completed),
                firstLoads[account.Key].Any(job => job.Status is JobStatus.Pending or JobStatus.Running or JobStatus.Failed or JobStatus.Deferred)))
            .ToList();

        return new DashboardState(states, jobs);
    }

    private static async Task<JobOwner> ConnectAsync(ISyncLane lane, ConnectedAccounts accounts, CancellationToken cancellationToken)
    {
        var account = accounts.Connect();
        await lane.PlanAsync(Queue, account, cancellationToken);
        return account;
    }
}
