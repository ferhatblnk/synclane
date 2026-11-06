using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace SyncLane.AspNetCore;

/// <summary>Maps SyncLane's monitor to HTTP.</summary>
public static partial class SyncLaneEndpoints
{
    private const string Producer = "SyncLane.AspNetCore";

    private static readonly string[] ForwardingHeaders = ["X-Forwarded-For", "X-Forwarded-Host", "X-Real-IP", "Forwarded"];

    // Fixed here rather than taken from the host: the same JSON whatever the host's settings, statuses as words.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Maps the monitor's reads, its repairs and a live stream of job changes under <paramref name="prefix"/>, and returns
    /// the group so the application can require authorization: <c>app.MapSyncLane().RequireAuthorization("ops")</c>.
    /// <para>
    /// Until the group requires authorization, every endpoint answers only requests from this machine, and a request that
    /// came through a proxy does not count as one. Repairs need the <see cref="SyncLaneEndpointOptions.ActionHeader"/>
    /// header. Answers are never cached and never hold a job's cursor. Needs <c>AddSyncLane</c>.
    /// </para>
    /// </summary>
    public static RouteGroupBuilder MapSyncLane(
        this IEndpointRouteBuilder endpoints, string prefix = "/synclane", Action<SyncLaneEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(prefix);

        var options = new SyncLaneEndpointOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(prefix).WithTags(options.Tag).ProducesProblem(StatusCodes.Status403Forbidden);
        group.AddEndpointFilter(LocalUnlessAuthorized);

        group.MapGet("/overview", GetOverviewAsync)
            .Produces<IReadOnlyList<QueueOverview>>().WithName("SyncLane_Overview").WithSummary("One line per queue: open jobs by state, owners, instances and the last day.");
        group.MapGet("/freshness", GetFreshnessAsync)
            .Produces<PageOf<OwnerFreshness>>().ProducesProblem(StatusCodes.Status400BadRequest).WithName("SyncLane_Freshness").WithSummary("Owners ranked by how old their completed data is, with the job that explains it. Search finds owners by name or key.");
        group.MapGet("/problems", GetProblemsAsync)
            .Produces<IReadOnlyList<ProblemGroup>>().WithName("SyncLane_Problems").WithSummary("Failing, deferred and abandoned jobs grouped by error code.");
        group.MapGet("/budgets", GetBudgetsAsync)
            .Produces<PageOf<BudgetState>>().ProducesProblem(StatusCodes.Status400BadRequest).WithName("SyncLane_Budgets").WithSummary("Each rate budget and key: learned rate, pause and last refusal.");
        group.MapGet("/instances", GetInstancesAsync)
            .Produces<IReadOnlyList<InstanceInfo>>().WithName("SyncLane_Instances").WithSummary("Every process of every application on the schema: alive, stopped, or gone without stopping.");
        group.MapGet("/volume", GetVolumeAsync)
            .Produces<IReadOnlyList<VolumeBucket>>().WithName("SyncLane_Volume").WithSummary("Completed jobs, records, failures and give-ups per hour or day.");
        group.MapGet("/jobs", GetJobsAsync)
            .Produces<PageOf<JobView>>().ProducesProblem(StatusCodes.Status400BadRequest).WithName("SyncLane_Jobs").WithSummary("Jobs by queue, owner, status, type or a search of owner names and keys, newest first. Cursors of jobs are never shown.");
        group.MapGet("/pauses", GetPausesAsync)
            .Produces<IReadOnlyList<PauseInfo>>().WithName("SyncLane_Pauses").WithSummary("The pauses in force: who paused what, why, and until when.");
        group.MapGet("/budget-limits", GetBudgetLimitsAsync)
            .Produces<IReadOnlyList<BudgetLimitInfo>>().WithName("SyncLane_BudgetLimits").WithSummary("Temporary limits on rate budgets: who set what, why, and until when.");
        group.MapGet("/actions", GetActionsAsync)
            .Produces<PageOf<OperatorAction>>().ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SyncLane_Actions").WithSummary("What operators did, newest first, with who did it and why.");
        group.MapGet("/stream", Stream)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream").WithName("SyncLane_Stream").WithSummary("Server-sent events: 'ready' once connected, then one 'job' event per created job or status change, from every instance.");

        if (!options.ReadOnly)
        {
            var repairs = group.MapGroup("").AddEndpointFilter(RequireActionHeader).ProducesProblem(StatusCodes.Status400BadRequest);
            repairs.MapPost("/jobs/{id:long}/requeue", RequeueJobAsync)
                .Produces<int>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).WithName("SyncLane_RequeueJob").WithSummary("Fresh attempts for one failed, deferred or abandoned job.");
            repairs.MapPost("/jobs/{id:long}/skip", SkipJobAsync)
                .Produces<int>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).WithName("SyncLane_SkipJob").WithSummary("Takes one job that is not running out of line for good, so an ordered lane goes on.");
            repairs.MapPost("/jobs/{id:long}/restart", RestartJobAsync)
                .Produces<int>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).WithName("SyncLane_RestartJob").WithSummary("Runs one job that is not running again from its first page.");
            repairs.MapPost("/requeue-by-code", RequeueByCodeAsync)
                .Produces<int>().WithName("SyncLane_RequeueByCode").WithSummary("Fresh attempts for every failing job of a queue with one error code.");
            repairs.MapPost("/pause", PauseAsync)
                .Produces<int>().ProducesProblem(StatusCodes.Status400BadRequest)
                .WithName("SyncLane_Pause").WithSummary("Emergency stop of a queue, or one owner on it: running jobs are cut and handed back.");
            repairs.MapPost("/resume", ResumeAsync)
                .Produces<int>().WithName("SyncLane_Resume").WithSummary("Lifts the pause of a queue, or one owner on it.");
            repairs.MapPost("/budgets/limit", LimitBudgetAsync)
                .Produces<int>().ProducesProblem(StatusCodes.Status400BadRequest)
                .WithName("SyncLane_LimitBudget").WithSummary("Lowers a rate budget, or one key of it, for a while. It never raises one.");
            repairs.MapPost("/budgets/clear-limit", ClearBudgetLimitAsync)
                .Produces<int>().WithName("SyncLane_ClearBudgetLimit").WithSummary("Lifts a temporary limit on a rate budget.");
            repairs.MapPost("/prioritize", PrioritizeOwnerAsync)
                .Produces<int>().WithName("SyncLane_PrioritizeOwner").WithSummary("Moves an owner's waiting jobs ahead of every other owner's.");
            repairs.MapPost("/sync-now", SyncNowAsync)
                .Produces<long>().WithName("SyncLane_SyncNow")
                .WithSummary("Fetches an owner's new data now, for example on a webhook; a burst of calls is one fetch.");
        }

        return group;
    }

    private static async Task<IResult> GetOverviewAsync(ISyncLaneMonitor monitor, CancellationToken cancellationToken) =>
        Answer(await monitor.GetOverviewAsync(cancellationToken));

    private static async Task<IResult> GetFreshnessAsync(
        ISyncLaneMonitor monitor, string? queue, string? owner, string? search, int? limit, string? cursor, CancellationToken cancellationToken) =>
        Answer(await monitor.GetFreshnessAsync(
            new FreshnessQuery { Queue = queue, Owner = owner, Search = search, Limit = limit ?? 50, Cursor = cursor }, cancellationToken));

    private static async Task<IResult> GetProblemsAsync(ISyncLaneMonitor monitor, string? queue, CancellationToken cancellationToken) =>
        Answer(await monitor.GetProblemsAsync(queue, cancellationToken));

    private static async Task<IResult> GetBudgetsAsync(
        ISyncLaneMonitor monitor, string? budget, int? limit, string? cursor, CancellationToken cancellationToken) =>
        Answer(await monitor.GetBudgetsAsync(new BudgetQuery { Budget = budget, Limit = limit ?? 50, Cursor = cursor }, cancellationToken));

    private static async Task<IResult> GetInstancesAsync(ISyncLaneMonitor monitor, CancellationToken cancellationToken) =>
        Answer(await monitor.GetInstancesAsync(cancellationToken));

    private static async Task<IResult> GetVolumeAsync(
        ISyncLaneMonitor monitor, DateTimeOffset from, DateTimeOffset? to, string? queue, string? type, VolumeUnit? unit,
        CancellationToken cancellationToken) =>
        Answer(await monitor.GetVolumeAsync(
            new VolumeQuery { From = from, To = to, Queue = queue, Type = type, Unit = unit ?? VolumeUnit.Hour }, cancellationToken));

    private static async Task<IResult> GetJobsAsync(
        ISyncLane syncLane, string? queue, string? owner, JobStatus? status, string? type, string? search, int? limit, string? cursor,
        CancellationToken cancellationToken)
    {
        var jobs = await syncLane.GetJobsAsync(
            new JobQuery { Queue = queue, Owner = owner, Status = status, Type = type, Search = search, Limit = limit ?? 50, Cursor = cursor },
            cancellationToken);
        return jobs.Succeeded
            ? TypedResults.Json(new PageOf<JobView>(jobs.Items.Select(JobView.Of).ToList(), jobs.NextCursor), Json)
            : Problem(jobs.Error!);
    }

    private static ServerSentEventsResult<string> Stream(ISyncLaneMonitor monitor, string? queue, string? owner, HttpContext http) =>
        TypedResults.ServerSentEvents(Changes(monitor, queue, owner, http.RequestAborted));

    /// <summary>
    /// A <c>ready</c> event as soon as the reader is in place, so the client knows it is connected and that nothing after
    /// that point is missed; then one <c>job</c> event per change. Without the first event no header would leave until
    /// the first change.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<string>> Changes(
        ISyncLaneMonitor monitor, string? queue, string? owner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var changes = monitor.WatchAsync(queue, owner, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var next = changes.MoveNextAsync();
        yield return new SseItem<string>("{}", "ready");

        // Sequential: forwarding one ordered stream.
        while (await next)
        {
            yield return new SseItem<string>(JsonSerializer.Serialize(changes.Current, Json), "job");
            next = changes.MoveNextAsync();
        }
    }

    private static async Task<IResult> RequeueJobAsync(long id, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.RequeueJobAsync(id, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "requeued job", id.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> SkipJobAsync(long id, string reason, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.SkipJobAsync(id, reason, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "skipped job", id.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> RestartJobAsync(long id, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.RestartJobAsync(id, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "restarted job", id.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> RequeueByCodeAsync(string queue, string code, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.RequeueByCodeAsync(queue, code, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "requeued by code", $"{queue} {code}", result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> PrioritizeOwnerAsync(string queue, string owner, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.PrioritizeOwnerAsync(queue, owner, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "prioritized owner", $"{queue} {owner}", result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> SyncNowAsync(
        string queue, string owner, string type, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.SyncNowAsync(queue, owner, type, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "synced now", $"{queue} {owner} {type}", result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> GetPausesAsync(ISyncLaneMonitor monitor, CancellationToken cancellationToken) =>
        Answer(await monitor.GetPausesAsync(cancellationToken));

    private static async Task<IResult> GetActionsAsync(
        ISyncLaneMonitor monitor, string? queue, string? owner, int? limit, string? cursor, CancellationToken cancellationToken) =>
        Answer(await monitor.GetActionsAsync(new ActionQuery { Queue = queue, Owner = owner, Limit = limit ?? 50, Cursor = cursor }, cancellationToken));

    private static async Task<IResult> PauseAsync(
        string queue, string? owner, string reason, DateTimeOffset? until, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.PauseAsync(queue, owner, reason, Who(http), until, http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "paused", $"{queue} {owner}", result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> ResumeAsync(string queue, string? owner, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.ResumeAsync(queue, owner, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "resumed", $"{queue} {owner}", result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> GetBudgetLimitsAsync(ISyncLaneMonitor monitor, CancellationToken cancellationToken) =>
        Answer(await monitor.GetBudgetLimitsAsync(cancellationToken));

    private static async Task<IResult> LimitBudgetAsync(
        string budget, string? key, int limit, TimeSpan per, string reason, DateTimeOffset? until,
        ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.LimitBudgetAsync(budget, key, limit, per, reason, Who(http), until, http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "limited budget", $"{budget} {key}", result.Succeeded);
        return Answer(result);
    }

    private static async Task<IResult> ClearBudgetLimitAsync(string budget, string? key, ISyncLaneMonitor monitor, HttpContext http, ILoggerFactory loggers)
    {
        var result = await monitor.ClearBudgetLimitAsync(budget, key, Who(http), http.RequestAborted);
        LogRepair(loggers.CreateLogger(Producer), Who(http), "cleared budget limit", $"{budget} {key}", result.Succeeded);
        return Answer(result);
    }

    private static IResult Answer<T>(SyncResult<T> result) =>
        result.Succeeded ? TypedResults.Json(result.Value, Json) : Problem(result.Error!);

    private static IResult Answer<T>(SyncListResult<T> result) =>
        result.Succeeded ? TypedResults.Json(new PageOf<T>(result.Items, result.NextCursor), Json) : Problem(result.Error!);

    private static IResult Problem(SyncError error, int? status = null) => TypedResults.Problem(
        detail: error.Message,
        statusCode: status ?? error.Kind.ToHttpStatusCode(),
        title: error.Code,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    private static async ValueTask<object?> LocalUnlessAuthorized(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";

        var metadata = http.GetEndpoint()?.Metadata;
        var authorized = metadata?.GetMetadata<IAuthorizeData>() is not null && metadata.GetMetadata<IAllowAnonymous>() is null;
        if (authorized || IsLocal(http))
            return await next(context);

        return Problem(EndpointErrors.RemoteWithoutAuthorization(), StatusCodes.Status403Forbidden);
    }

    private static async ValueTask<object?> RequireActionHeader(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Request.Headers.ContainsKey(SyncLaneEndpointOptions.ActionHeader)
            ? await next(context)
            : Problem(EndpointErrors.ActionHeaderMissing(SyncLaneEndpointOptions.ActionHeader));

    /// <summary>A request from this machine, not passed on by a proxy: a proxy on the same machine would otherwise open the door to everyone.</summary>
    private static bool IsLocal(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } remote
        && IPAddress.IsLoopback(remote)
        && !ForwardingHeaders.Any(header => http.Request.Headers.ContainsKey(header));

    private static string Who(HttpContext http) => http.User.Identity?.Name ?? "anonymous";

    [LoggerMessage(Level = LogLevel.Information, Message = "{User} {Action} {Target} through the SyncLane endpoints; succeeded: {Succeeded}.")]
    private static partial void LogRepair(ILogger logger, string user, string action, string target, bool succeeded);
}
