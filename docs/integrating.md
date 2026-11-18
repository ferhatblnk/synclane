# Integrating SyncLane into an application

This page takes an existing .NET application from nothing to data flowing in production, step by step. It is written so that you can hand it to an AI assistant in your project ("read this page and integrate SyncLane"), and so that a developer can follow it alone. Every call on it exists in the current version.

The order is fixed: answer the questions, make the decisions, follow the steps, check each one, then run the production trial. Nothing below asks you to change SyncLane; everything happens in your application.

## For an AI assistant reading this page

- **Ask before you write code.** Work through [the questions](#1-questions-to-answer-first) with the user. Where the user does not know, take the default in the table and say that you did.
- **Use only what is documented.** The calls, settings and types on this page and the pages it links to. If something seems to be missing, say so; do not invent an overload or a setting.
- **Follow the project's conventions.** Its layers, folder structure, DI style, naming and test framework. SyncLane's own repository rules (its `CLAUDE.md`) are for changing SyncLane, not for applications that use it.
- **Never put a credential anywhere you write.** Not in code, a recipe, `appsettings.json`, a test, a curl you suggest or a reply. Credentials come from the project's own storage through an `ICredentialStore`, or from an environment variable while testing.
- **Finish each step before the next.** Each step ends with "Done when"; check it, and report what you saw.
- **Stop and ask** when a step's check fails twice, when the project's database is not PostgreSQL 14 or later, or when the target framework is below .NET 10.

## 1. Questions to answer first

| # | Question | Why it matters | Default when nobody knows |
| --- | --- | --- | --- |
| Q1 | Which external API, and which resources of it (orders, products, invoices)? | Each resource becomes a job type. | None; this one must be answered. |
| Q2 | For each resource: is it read by **time range** (new and changed records since a date), as a **full list** every time, or only **on demand**? | Decides `Windowed`, `Snapshot` or `Handle` ([step 5](#step-5-declare-the-queue)). | Time range, if the API takes a date filter. |
| Q3 | Who is an **owner**: a connected account, store, tenant? Where are they stored, and what is a key that never changes? | The planner plans per owner. A key that changes reloads the owner's history. | The primary key of the table that holds connected accounts. |
| Q4 | Do several owners share one set of credentials or one rate limit? | Owners that share a limit must share a lane and a budget key. | No: every owner has its own lane and key. |
| Q5 | What rate limit does the API publish, per account? | It becomes the rate budget. | A generous ceiling with `start: 0.25`; the budget learns below it. |
| Q6 | How much history is needed, and how many records does the largest owner have per month? | Sets `horizon` and `slice`. One slice is one job, the largest unit of work that cannot be split. | Horizon 30 days and slice 7 days for the trial; raise later, only the gap is fetched. |
| Q7 | How fresh must the data be? Can records appear late, with an older date? | Sets `every` and `overlap`. | `every` 15 minutes, `overlap` 10 minutes. |
| Q8 | Is the API a plain JSON list, page by page, or does it need business logic (several calls per page, reports, GraphQL, an SDK the project already uses)? | Recipe or handler ([decision D1](#d1-handler-or-recipe)). | Handler, if in doubt. |
| Q9 | Where do the records go: tables the project already has, or new ones? | The handler or the sink writes them. Writes must be upserts. | The project's own tables, through its own data layer. |
| Q10 | Where are the API credentials stored today, and who refreshes them? | The handler's client or an `ICredentialStore` reads them from there. | Where they are today; SyncLane stores none. |
| Q11 | Which PostgreSQL database, and is there a connection pooler (PgBouncer) in front of it? | A pooler in transaction mode needs `ListenConnectionString`. | The application's own database, no pooler. |
| Q12 | Who changes the database schema: the application at start-up, or the project's migrations? | `CreateSchema`. | SyncLane at start-up (`CreateSchema = true`). |
| Q13 | How is the application deployed: one process, or web instances and a separate worker? | Who runs the planner and the workers ([decision D2](#d2-which-process-does-what)). | One process does everything. |
| Q14 | Does another system already sync this data? | Its history is marked loaded instead of fetched again ([step 11](#step-11-move-from-an-existing-sync)). | No. |
| Q15 | Should users trigger a refresh (a button, a webhook)? | `SyncNowAsync` ([step 9](#step-9-refresh-on-demand-and-new-accounts)). | No. |
| Q16 | Who may see and repair jobs, and through what? | The monitor over HTTP, behind an authorization policy ([step 8](#step-8-the-monitor-over-http)). | Nobody yet; add it before production. |

## 2. Decisions

### D1. Handler or recipe

| Use a **recipe** when | Use a **handler** when |
| --- | --- |
| The API returns JSON records page by page, and you only fetch them and store them. | A page needs business logic: several calls, a report to request and wait for, a GraphQL query, an SDK or client the project already has. |
| Paging is by page number, offset, cursor, next URL or `Link` header. | Records must be transformed or validated with the project's rules before they are stored. |
| Authentication is bearer, basic, an API key, OAuth 2.0 or a request signer. | The data goes to several tables in one transaction. |

A recipe is a JSON file; [Recipes](recipes.md) describes every part, and `synclane init` writes one from a curl that works ([with an AI assistant](ai.md)). A handler is a class with one method. One queue can mix both: each job type chooses.

### D2. Which process does what

| Layout | When | Settings |
| --- | --- | --- |
| **One process** | Small and medium loads; the default. | Nothing to set. |
| **Web and worker, one image** | Syncing must not take CPU or memory from user requests. | The same build runs as two containers. Web: `SyncLane__RunWorkers=false`, `SyncLane__RunPlanner=false`. Worker: both `true` (the default). |
| **Several workers** | More owners loading side by side. | Run more copies of the worker. Jobs, planning and budgets are shared through the table; nothing else to configure. |

- Keep one application per queue. Copies of one application share their queues; a second application that runs the same queue is refused at start-up (`SL1011`). An instance with both `RunWorkers` and `RunPlanner` off claims no queue and may call `ISyncLane` and the monitor freely.
- Run one process per container. If a worker and a web host share a container and one of them dies, the orchestrator does not restart it.
- The worker needs no open port. Its state is in the database: the monitor reads it from any instance.
- Work the user waits for (an answer on screen) stays in the web process. Work nobody waits for (fetching, reports, sending data out) becomes a job, and a "refresh" button enqueues it and returns at once.

### D3. Owner, lane and budget key

- **Owner key:** stable and unique per queue, usually the connected account's id. Never a name or an email.
- **Lane:** leave it out, and each owner gets its own lane. Give owners that share credentials or a rate limit the same lane, so their jobs never run at the same time.
- **Budget key:** usually the owner key. Owners that share one rate limit pass one key.
- **Name:** what people call the owner, shown and searched by the monitor. It may change at any time.
- **Values:** what a recipe needs besides the key, such as a region or a marketplace, as `JobOwner.Values`.

## 3. Steps

### Step 1. Add the packages

```bash
dotnet add package SyncLane
dotnet add package SyncLane.Http          # only for recipes
dotnet add package SyncLane.AspNetCore    # only for the monitor over HTTP
```

**Done when** the project builds.

### Step 2. Connection and configuration

Add a connection string, and a `SyncLane` section for the numbers that change per environment:

```json
{
  "ConnectionStrings": { "Postgres": "Host=db;Database=app;Username=app" },
  "SyncLane": {
    "Queues": {
      "shops": {
        "Concurrency": 2,
        "Types": { "orders": { "Every": "00:15:00" } }
      }
    }
  }
}
```

The password comes from where the project keeps secrets, never from a committed file. Behind PgBouncer in transaction mode, also set `SyncLane:ListenConnectionString` to a connection that goes around the pooler, or set `SyncLane:ListenForChanges` to `false` ([Behind a connection pooler](operations.md#behind-a-connection-pooler)).

**Done when** the application starts with the section present and the connection string resolves.

### Step 3. The owner source

One class that returns who is connected now. The planner calls it once a minute.

```csharp
public sealed class ConnectedShops(AppDb db) : IOwnerSource
{
    public async Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
        await db.Shops
            .Where(shop => shop.IsConnected)
            .Select(shop => new JobOwner(shop.Id.ToString(), Name: shop.Name)
            {
                Priority = shop.IsNew ? JobPriority.High : JobPriority.Normal   // a new account's history loads first
            })
            .ToListAsync(cancellationToken);
}
```

- Return only owners that should have jobs. An owner left out gets no new jobs; the jobs it already has still run until they are cancelled ([step 10](#step-10-accounts-that-disconnect-or-leave)).
- It is resolved in a scope of its own, so a scoped `DbContext` is fine.

**Done when** it returns the expected owners in a test or a debug run.

### Step 4a. A handler

One page per call. SyncLane saves the returned cursor after every page and calls again until the cursor is `null`.

```csharp
public sealed class OrderHandler(ShopClient client, OrderRepository orders, RateBudgets budgets) : IJobHandler
{
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        var permit = await budgets.WaitAsync("shop-api", job.Owner, cancellationToken);
        var response = await client.GetOrdersAsync(job.Owner, job.WindowStart, job.WindowEnd, job.Cursor, cancellationToken);

        if (response.IsThrottled)
        {
            var pause = await budgets.ThrottledAsync(permit, response.RetryAfter, cancellationToken);
            throw new JobPostponedException("The shop API asked to slow down.", pause);
        }

        if (response.IsUnauthorized)
            throw new JobAbandonedException("The shop's access was revoked; reconnect it.");

        await orders.UpsertAsync(job.Owner, response.Orders, cancellationToken);
        return new JobPage(response.NextCursor, response.Orders.Count);
    }
}
```

The rules of a handler:

- **One page, then return.** Never loop over every page inside one call; the cursor would be saved only at the end.
- **Upsert, never insert.** A page can run twice after a crash (at least once, not exactly once).
- **Read the window and the cursor from the job:** `WindowStart` and `WindowEnd` (`null` for a snapshot), `Cursor` (`null` on the first page).
- **Put no credential in the cursor.** It is stored in the table.
- **Draw a token before every request** when the API has a rate limit, with the same budget name everywhere that calls it, the web process included.
- **Say what happened** with the three exceptions: `JobPostponedException` to wait without using an attempt, `JobThrottledException` when there is no budget, `JobAbandonedException` when a retry cannot help. Any other exception is a failure that is retried with backoff.
- **Pass the cancellation token on**, and do not catch `OperationCanceledException`.
- Each page runs in a fresh DI scope; scoped services are new for every page.

**Done when** a unit test with a fake client shows: the first page reads `job.Cursor = null`, the last page returns `NextCursor = null`, a `429` throws `JobPostponedException`, and the same page written twice leaves one row.

### Step 4b. A recipe

Write the recipe with `synclane init` from a curl that works, check it with `synclane test` (two pages, no database), and keep it in the project, for example `recipes/orders.jsonc`. Then a credential store over the project's own storage:

```csharp
public sealed class ShopCredentials(AppDb db) : ICredentialStore
{
    public async ValueTask<Credential?> GetAsync(CredentialKey key, CancellationToken cancellationToken)
    {
        var shop = await db.Shops.FindAsync(new object[] { int.Parse(key.Owner) }, cancellationToken);
        return shop is { IsConnected: true } ? new Credential { Token = shop.AccessToken } : null;
    }

    public async ValueTask SaveRefreshTokenAsync(CredentialKey key, string refreshToken, CancellationToken cancellationToken)
    {
        var shop = await db.Shops.FindAsync(new object[] { int.Parse(key.Owner) }, cancellationToken);
        shop!.RefreshToken = refreshToken;
        await db.SaveChangesAsync(cancellationToken);
    }
}
```

`null` means the owner has no credential any more and abandons the job (`SL5119`); an exception, such as a vault that cannot be reached, retries it. Register the recipe and say where its records go:

```csharp
builder.Services.AddSyncLaneHttp(http =>
{
    http.Credentials<ShopCredentials>();
    http.Recipe("orders", File.ReadAllText("recipes/orders.jsonc"))
        .WriteToPostgres("shop_orders", table => table.VersionedBy("$.updated_at"));   // or .WriteTo<OrderSink>()
});
```

`WriteToPostgres` creates the table and upserts one row per owner and record. For the project's own tables, implement `IRecordSink` (one method, `WriteAsync(RecordPage page, ...)`) and upsert there.

**Done when** `synclane test recipes/orders.jsonc` reads two pages and the application starts without a recipe error.

### Step 5. Declare the queue

```csharp
builder.Services.AddSyncLane(builder.Configuration.GetSection("SyncLane"), lane =>
{
    lane.ConnectionString = builder.Configuration.GetConnectionString("Postgres")!;
    lane.AddBudget("shop-api", limit: 2, per: TimeSpan.FromSeconds(1), burst: 10);

    lane.AddQueue("shops", queue => queue
        .Owners<ConnectedShops>()
        .Windowed<OrderHandler>("orders", horizon: TimeSpan.FromDays(30), slice: TimeSpan.FromDays(7),
            every: TimeSpan.FromMinutes(15), overlap: TimeSpan.FromMinutes(10))
        .Snapshot<ProductHandler>("products", every: TimeSpan.FromHours(6))
        .Handle<InvoiceHandler>("invoice-upload"));
});
```

- `Windowed`: history in slices back to `horizon`, newest first, then one job per `every` from where the data ends.
- `Snapshot`: one job that reads everything, then one per `every`.
- `Handle`: never planned; runs only when enqueued.
- A recipe's job type uses `HttpRecipeHandler` as its handler.
- Types run in the order they are declared, within a priority.
- Declare the same budgets in every process that calls the API, the web process included.

**Done when** the application starts, the log shows no `SL1xxx` error, and `synclane.jobs` has rows for the owners from step 3.

### Step 6. Split the roles, if you chose to

For the web and worker layout ([D2](#d2-which-process-does-what)), the same image runs twice:

```yaml
web:
  image: app
  environment:
    SyncLane__RunWorkers: "false"
    SyncLane__RunPlanner: "false"
worker:
  image: app
  # the defaults: plans and runs
```

Background services of the project that must run once, not in every web copy, belong to the worker role too: register them only when `RunWorkers` is on.

```csharp
if (builder.Configuration.GetValue("SyncLane:RunWorkers", true))
    builder.Services.AddHostedService<ExpiredDataCleanup>();
```

**Done when** jobs complete with the web instances' `RunWorkers` off, and `GetInstancesAsync()` lists both roles.

### Step 7. Health check and telemetry

```csharp
builder.Services.AddHealthChecks().AddCheck<SyncLaneHealthCheck>("synclane");
app.MapHealthChecks("/health");

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(SyncLaneTelemetry.MeterName))
    .WithTracing(tracing => tracing.AddSource(SyncLaneTelemetry.ActivitySourceName));
```

A worker without an HTTP port reports through its telemetry and its row in `synclane.instances`; give it a health endpoint only if the orchestrator needs one. Set the alerts listed in [Metrics and traces](operations.md#metrics-and-traces).

**Done when** `/health` answers `Healthy` (or `Degraded` with a description you understand, such as polling behind a pooler).

### Step 8. The monitor over HTTP

```csharp
app.MapSyncLane("/synclane").RequireAuthorization("ops");
```

It answers only requests from the same machine until it requires authorization. Every action (pause, requeue, skip, sync now) is logged with who and why. The routes are in [the package's readme](../src/SyncLane.AspNetCore/README.md); in code, inject `ISyncLaneMonitor` ([Watching it run](monitoring.md)).

**Done when** `GET /synclane/overview` returns the queue with an authorized user, and is refused without one.

### Step 9. Refresh on demand and new accounts

```csharp
// A "refresh" button or a webhook: fetch from where the owner's data ends to now, at high priority.
// A burst of calls is one fetch. Returns at once; the worker does the work.
await monitor.SyncNowAsync("shops", owner: shop.Id.ToString(), type: "orders", by: user.Id);

// An account that just connected: plan it now instead of at the next round.
await syncLane.PlanAsync("shops", new JobOwner(shop.Id.ToString(), Name: shop.Name));

// "Connecting..." on screen without a status column of your own.
var progress = await syncLane.GetProgressAsync("shops", shop.Id.ToString());
```

Every call returns a result: check `Succeeded`, and map `Error.Kind` with `ToHttpStatusCode()` in an endpoint ([Working with jobs](jobs.md#results-and-errors)).

**Done when** pressing the button creates one job, and pressing it five times quickly still creates one.

### Step 10. Accounts that disconnect or leave

| Event | Call |
| --- | --- |
| Disconnected, may come back | Leave it out of the owner source, then `syncLane.CancelAsync(queue, owner, reason)`. When it comes back: `RequeueAsync(queue, owner)`; its slices continue from their cursors. |
| Removed for good | Leave it out of the owner source, then `syncLane.ForgetOwnerAsync(queue, owner)`, then delete its data. |

Never delete job rows by hand: the planner's summary would stay, and an owner that came back would never load its history again.

**Done when** a disconnected test owner has no pending jobs, and a reconnected one continues.

### Step 11. Move from an existing sync

If another system already loaded the history, record it so it is not fetched again. Mark only ranges without holes.

1. Deploy with `RunPlanner` and `RunWorkers` off.
2. Call `syncLane.MarkLoadedAsync(queue, ranges)` with what the old system loaded ([Moving from another system](operations.md#moving-from-another-system)).
3. Stop the old system.
4. Turn the planner and the workers on.

**Done when** the first jobs of a migrated owner cover only what is older than its marked range and what came after.

### Step 12. Deploy

- **Schema:** with `CreateSchema = true` the first instance creates it and applies migrations behind a lock. With the project's own migrations, add `migrationBuilder.Sql(SyncLaneSchema.Script("synclane"))` now, and again in a new migration with each SyncLane upgrade that adds one ([Running the schema yourself](operations.md#running-the-schema-yourself)).
- **Rolling deploys** are safe: migrations only add, and an older instance keeps working against the newer schema.
- **Clocks:** keep the hosts' clocks in sync; leases and budgets use them.

**Done when** two copies of the worker run side by side for a day with every job created once and never two running jobs in one lane.

## 4. The first run

What a healthy first hour looks like:

```sql
SELECT type, status, count(*) FROM synclane.jobs GROUP BY type, status ORDER BY type, status;
```

- Jobs move from `pending` to `running` to `completed`; recent slices complete first.
- `GetProblemsAsync()` is empty, or groups the failures by error code; [Error codes](errors.md) says what each means and how to fix it.
- The project's tables fill, and running the same window again changes no row count.
- `GetBudgetsAsync(new BudgetQuery())` shows each budget's rate; frequent `429`s mean the declared limit is too high.

## 5. Before production

Run the [production trial](production-trial.md): one integration that is allowed to fail, slow settings, the old path beside it for two weeks, and the drills (graceful stop, `kill -9`, rolling deploy, two instances, provider outage, revoked access).

## Mistakes we have seen

| Mistake | What happens | Instead |
| --- | --- | --- |
| An owner key that can change, such as a name | The owner's history loads again under the new key. | A stable id; the name goes in `JobOwner.Name`. |
| Inserting records | A page that runs twice duplicates them. | Upsert by the record's id. |
| A slice that takes hours for the biggest owner | A failure late in the slice repeats most of it. | A slice that finishes in minutes at the API's rate. |
| Looping over all pages in one handler call | Nothing is saved until the end; a crash starts over. | Return after one page. |
| A budget declared only in the worker | The web process's calls do not count against the limit. | The same `AddBudget` in every process that calls the API. |
| Two applications running one queue | The second one is refused at start-up (`SL1011`). | One application per queue, or one `Application` name. |
| `RunPlanner` off in every process | No jobs are created. | At least one process plans. |
| Listening through PgBouncer in transaction mode | Live changes never arrive, and other clients see timeouts. | `ListenConnectionString` around the pooler, or `ListenForChanges = false`. |
| Deleting job rows by hand | A returning owner never loads its history again. | `ForgetOwnerAsync`. |
| A token in a recipe, a test or `appsettings.json` | A secret in the repository. | `ICredentialStore`, or an environment variable while testing. |

## Checklist

- [ ] The questions in section 1 are answered, or their defaults written down.
- [ ] Owner keys are stable; owners that share a limit share a lane and a budget key.
- [ ] Every write is an upsert; a page run twice changes nothing.
- [ ] Each handler returns after one page, uses the job's window and cursor, and throws the right exception for `429` and revoked access.
- [ ] Budgets are declared in every process that calls the API.
- [ ] No credential in code, recipes, configuration files, tests or cursors.
- [ ] Roles are set; one process per container; at least one process plans.
- [ ] Health check, telemetry and alerts are wired.
- [ ] The monitor requires authorization.
- [ ] Disconnect and removal call `CancelAsync` and `ForgetOwnerAsync`.
- [ ] The production trial has started.
