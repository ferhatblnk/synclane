# Watching it run

How to see what SyncLane does: the monitor's questions, live changes, the HTTP endpoints, metrics and traces, and health checks. Repairs and SQL for everything else are in [operations.md](operations.md).

`ISyncLaneMonitor` answers what an operator asks, for every queue in the schema, whichever application runs it:

| Call | Answers |
| --- | --- |
| `GetOverviewAsync()` | One line per queue: due, postponed, running, retrying, deferred and abandoned jobs; owners; live instances; jobs, records and failures in the last 24 hours. |
| `GetFreshnessAsync(query)` | Owners ranked by how old their newest completed data is, each with the job that explains it (its status, error code and next run) and how much of its history has loaded. `Search` finds owners by name or key. |
| `GetFreshnessOwnersAsync(query)` | The same rows grouped by owner across the queues you name, the owner with the oldest data first, so a page never cuts an owner in half. `Show` keeps only the rows, and so the owners, that are stale, need attention (an open job failed, was deferred or abandoned) or are paused. `IncludeTotal` counts the owners. |
| `GetProblemsAsync(queue?)` | Failing, deferred and abandoned jobs grouped by error code: "SL5106: 37 jobs, 12 owners" instead of 37 rows. |
| `GetBudgetsAsync(query)` | Each rate budget and key: the rate it learned, the limit the API reports and any operator's limit, whether it is paused, when it was last refused. `Search` finds buckets whose budget name or key contains the text, ignoring case; `IncludeTotal` also counts the matching buckets into `Total`, for a pager that shows "page 2 of 7". |
| `GetInstancesAsync()` | Every process of every application: host, role, version, queues, whether it runs workers, the planner and listening, and whether it is alive, stopped (and when) or gone without stopping. Stopped and gone ones stay listed for a day. `SharesNameWith` names live instances under the same application that declare none of its queues and job types: most likely another application that took the same name. `FollowsSwitches` is false for an instance of a version that does not read switches. |
| `GetSwitchesAsync()` | Each application and role: the switch an operator set, if any, how many of its live instances run workers and the planner, and how many run a version that ignores switches (`IgnoringSwitch`). |
| `GetVolumeAsync(query)` | Completed jobs, records, failed attempts and abandoned jobs per hour or day, kept for 400 days. |

```csharp
var behind = await monitor.GetFreshnessAsync(new FreshnessQuery { Queue = "shops", Limit = 20 });
foreach (var row in behind.Items)
    Console.WriteLine($"{row.Owner} {row.Type}: data until {row.DataUntil:u}, {row.Status} {row.Code}");
```

An owner is known by its key on one queue. When an integration runs a queue per kind of data with the same owner keys on each, name those queues and each account comes once, with a row per queue and job type. A row is stale when its completed data is older than `StaleAfter` (24 hours by default) or it has completed nothing yet:

```csharp
var owners = await monitor.GetFreshnessOwnersAsync(new FreshnessOwnerQuery
{
    Queues = ["shops", "shops.products"],
    Show = FreshnessShow.Attention,
    IncludeTotal = true
});
Console.WriteLine($"{owners.Total} owners need a look");
foreach (var owner in owners.Items)
    Console.WriteLine($"{owner.OwnerName ?? owner.Owner}: {string.Join(", ", owner.Rows.Select(row => $"{row.Queue} {row.Type} {row.Code}"))}");
```

`WatchAsync(queue?, owner?)` streams jobs as they are created and change status, from every instance, a moment after each change commits, so a live view or an alert needs no polling:

```csharp
await foreach (var change in monitor.WatchAsync("shops", cancellationToken: stopping))
    await hub.Clients.All.SendAsync("job", change.Id, change.Owner, change.Status.ToString(), stopping);
```

Over HTTP, `SyncLane.AspNetCore` maps all of it in one line, with the stream as server-sent events. The endpoints show up in your OpenAPI (Swagger) document under `SyncLane`, and answer only requests from the same machine until you require authorization:

```csharp
app.MapSyncLane("/synclane").RequireAuthorization("ops");
```

See [the package's readme](../src/SyncLane.AspNetCore/README.md) for the routes.

`SetSwitchAsync(application, role, runWorkers, runPlanner, reason)` turns an application's workers and planner on or off without a deploy, and `SetListeningAsync(application, role, listen, reason)` its listening connections ([Switching an application on and off](operations.md#switching-an-application-on-and-off)). It also has an emergency stop, `PauseAsync(queue, owner?, reason)`, which cuts what runs on every instance and takes nothing more until `ResumeAsync`, and the repairs the answers call for: `RequeueJobAsync(id)`, `RestartJobAsync(id)` to fetch a window again, `RequeueByCodeAsync(queue, code)` after fixing what a problem group shows, `PrioritizeOwnerAsync(queue, owner)` for an account that should load first, and `SyncNowAsync(queue, owner, type)` when the other side announces a change, for example with a webhook: it fetches from where the owner's data reaches to now, at high priority, and a burst of calls is one fetch. `LimitBudgetAsync` lowers a rate budget for a while. Every action is logged with who did it and why, and `GetActionsAsync(query)` lists the log newest first: `Search` finds text in the action, queue, owner, target, reason or who did it (ignoring case, 1 to 100 characters), and `IncludeTotal` also counts every match, so a screen can show "page 2 of 7" (`total` over HTTP, with `includeTotal=true`). See [docs/operations.md](operations.md#changing).

Each read is one round trip that reads small summary tables and the open jobs, never the history: on a table of a million jobs each one takes about a millisecond, a page of 50 owners grouped across two queues about 6, and a year of daily volume about 45. Lists page with a cursor. `Total` is counted only when the query sets `IncludeTotal`, in the same round trip as the page; ask for it on the first page of a screen, not on every page. Searching budgets and counting them reads the small `rate_buckets` table (one row per budget and key): about 2 ms for 5,000 rows and 19 ms for 50,000.

SyncLane also reports metrics and traces through the APIs built into .NET, so it adds no dependency and costs nothing when nobody listens. With OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(SyncLaneTelemetry.MeterName))
    .WithTracing(tracing => tracing.AddSource(SyncLaneTelemetry.ActivitySourceName));
```

Every run of a job is a `synclane.job` span and every page a `synclane.page` span inside it, so the HTTP calls your handler makes show up under the page that made them. The metrics count jobs taken and finished by outcome, pages, records, throttles and planner runs, and time how long jobs wait and run. Tags name the queue and the job type, never the owner or the lane. Without OpenTelemetry, `dotnet-counters monitor --counters SyncLane` shows them live. The full list is in [docs/operations.md](operations.md#metrics-and-traces).

## Health checks

`AddSyncLane` provides `SyncLaneHealthCheck` for .NET's health checks, in a web app or a worker:

```csharp
builder.Services.AddHealthChecks().AddCheck<SyncLaneHealthCheck>("synclane");
app.MapHealthChecks("/health");
```

- **Unhealthy** when the database cannot be reached, its schema lacks migrations this version needs, or this instance has not renewed its registration for two minutes, after which other instances count it as gone.
- **Degraded** while it starts, when the latest planning round of a queue failed, or when it does not hear job changes (behind a pooler in transaction mode without `ListenConnectionString`, or while it reconnects). Workers still find jobs by polling then.
- Each check is one small query. Descriptions say what is wrong and how to fix it; they never hold a connection string or an exception's message.
