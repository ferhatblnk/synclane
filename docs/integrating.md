# Integrating SyncLane into an application

This page takes an existing .NET application from nothing to data flowing in production, step by step. It is written so that you can hand it to an AI assistant in your project ("read this page and integrate SyncLane"), and so that a developer can follow it alone. Every call on it exists in the current version.

The order is fixed: write down what the project does today, answer the questions, make the decisions, follow the steps with a check after each, prove the data is the same as before, then run the production trial. Nothing below asks you to change SyncLane; everything happens in your application.

## For an AI assistant reading this page

- **The project's own rules come first.** Before anything else, read the project's `CLAUDE.md` and `ARCHITECTURE.md`, and any file of the same kind it has (`AGENTS.md`, `CONTRIBUTING.md`, `.editorconfig`, a coding standard). Everything on this page is applied inside those rules, never over them: where they disagree, follow the project's rule and tell the user which part of this page gave way. If the project keeps a decision log, record the decisions of [section 3](#3-decisions) there, in its format.
- **Write no comments in the code.** Names and small methods say what the code does; the reasons go in the pull request. The one exception is a comment the project's own rules require, such as XML documentation on public members. `synclane init` writes a comment beside every guess in a recipe draft: confirm each guess with the user, then remove its comment.
- **Keep what the project does.** The integration changes when and how the work runs, not what is fetched, how it is mapped, or what the business rules decide. Write down today's behaviour first ([section 1](#1-what-the-project-does-today)) and prove at the end that the data is the same ([section 5](#5-prove-it)). Speed may improve; results may not change.
- **Leave the existing tests alone.** Every test that passed before passes after, unchanged. Never edit, skip or delete a test to make it pass: a test that fails means the behaviour changed.
- **If the project already uses SyncLane, start at [section 7](#7-upgrading-an-existing-integration).** The steps of section 4 are for a project without it; applied to a running integration, some of them would reload its history or set its web instances to work.
- **Ask before you write code.** Work through [the questions](#2-questions-to-answer-first) with the user. Where the user does not know, take the default in the table and say that you did.
- **Use only what is documented.** The calls, settings and types on this page and the pages it links to. If something seems to be missing, say so; do not invent an overload or a setting.
- **Follow the project's conventions.** Its layers, folder structure, DI style, naming and test framework. SyncLane's own repository rules (SyncLane's `CLAUDE.md`) are for changing SyncLane, not for applications that use it.
- **Never put a credential anywhere you write.** Not in code, a recipe, `appsettings.json`, a test, a curl you suggest or a reply. Credentials come from the project's own storage through an `ICredentialStore`, or from an environment variable while testing.
- **Finish each step before the next.** Each step ends with "Done when"; check it, and report what you saw.
- **Stop and ask** when a step's check fails twice, when the project's database is not PostgreSQL 14 or later, or when the target framework is below .NET 10.

## 1. What the project does today

Before any decision, write down every piece of sync work the project does now: scheduled jobs, hosted services, timers, "refresh" endpoints. One row each. The table is the specification of the integration, and it goes into the pull request.

| Column | Example |
| --- | --- |
| Name and where it lives | `OrderSyncJob`, a recurring job every 15 minutes |
| What it calls | `GET /orders?updated_after=` for each connected shop, 100 per page |
| For which accounts | shops that are connected and have an active subscription |
| Rules on the way | skips draft orders; converts the currency; computes the net total with `PriceCalculator` |
| Where it writes, by which key | `Orders`, upsert by `(ShopId, ExternalId)`; `OrderItems` replaced per order |
| Side effects | publishes `OrderImported`; sends a low-stock email |
| Time range | since the last successful run, minus 5 minutes |
| How it fails today | retries 3 times, then logs |
| Who starts it by hand | the "refresh" button on the orders page |

**Must not change:** which records are fetched (accounts, filters, time ranges), how they are mapped and validated, the keys they are stored under, and the side effects.

**May change:** when and how often the work runs (within what users expect), how many accounts load side by side, retries and backoff, and speed.

How the rows translate:
- Each row becomes a job type ([step 5](#step-5-declare-the-queue)), or stays where it is when users wait for its answer ([D2](#d2-which-process-does-what)).
- Each rule moves into the handler by **calling the code that implements it today**, not by copying it. A business rule keeps living in one place.
- "Since the last run" becomes the job's window: SyncLane starts each recurring window where the last one ended, and `overlap` replaces the "minus 5 minutes".
- "Retries 3 times, then logs" becomes the failure policy: retries with backoff, then `deferred`, then `abandoned`, all visible in the monitor.

## 2. Questions to answer first

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
| Q17 | What do the other applications on the same schema call themselves, and their queues? | The application's name and queue names must be its own ([D2](#d2-which-process-does-what)); `GetInstancesAsync` lists those already registered. | A short lower-case name of the project, such as `shop`, and queues starting with it. |

## 3. Decisions

### D1. Handler or recipe

| Use a **recipe** when | Use a **handler** when |
| --- | --- |
| The API returns JSON records page by page, and you only fetch them and store them. | A page needs business logic: several calls, a report to request and wait for, a GraphQL query, an SDK or client the project already has. |
| Paging is by page number, offset, cursor, next URL or `Link` header. | Records must be transformed or validated with the project's rules before they are stored. |
| Authentication is bearer, basic, an API key, OAuth 2.0 or a request signer. | The data goes to several tables in one transaction. |

Decide per row of [the inventory](#1-what-the-project-does-today) and write the decision into it. A row with any rule, mapping or side effect is a handler: the rule stays in the project's code and the handler calls it. A recipe is a JSON file; [Recipes](recipes.md) describes every part, and `synclane init` writes one from a curl that works ([with an AI assistant](ai.md)). A handler is a class with one method. One queue can mix both: each job type chooses.

### D2. Which process does what

**What a process is comes from its settings; whether it works comes from the database.** The settings name the process: its application (`SyncLane:Application` in `appsettings.json`, committed) and, when one build runs as web and worker, its role (`SyncLane__Role` in the worker's environment). They never say whether it runs workers or the planner: leave `RunWorkers` and `RunPlanner` out, at their default `true`, and decide from the monitor with a switch per application and role, kept in the database ([Switching an application on and off](operations.md#switching-an-application-on-and-off)). A switch survives restarts, deploys and new replicas, because each instance reads it when it starts, before it takes a job; it changes without a deploy, and every change is logged with who and why.

| What | Where | Example |
| --- | --- | --- |
| The application's name, its own on the schema | `appsettings.json`, committed | `"SyncLane": { "Application": "shop" }` |
| The role, only when one build runs as web and worker | the worker's environment | `SyncLane__Role: "worker"` |
| Names of new queues, starting with the application's name | code ([step 5](#step-5-declare-the-queue)) | `shop.stores` |
| Whether it runs workers and the planner | a switch in the database, set from the monitor | `POST /synclane/switch?application=shop&workers=false&reason=…` |
| Whether it listens for job changes | a switch of its own, set from the monitor | `POST /synclane/switch/listen?application=shop&listen=false&reason=…` |

| Layout | When | Settings |
| --- | --- | --- |
| **One process** | Small and medium loads; the default. | Nothing to set. |
| **Web and worker, one image** | Syncing must not take CPU or memory from user requests. | The same build runs as two containers; the worker's environment gives it a role, `SyncLane__Role=worker`. Before the first deploy, the web instances (no role) are switched off and the worker role on, from the monitor ([step 6](#step-6-split-the-roles-if-you-chose-to)). Nothing in `appsettings.json` turns anything off. |
| **Several workers** | More owners loading side by side. | Run more copies of the worker. Jobs, planning and budgets are shared through the table; nothing else to configure. |

- Give the application a name of its own. The default is the entry assembly's name, and projects built from one template often share it (`Api`, `WebApi`): two applications under one name are one to SyncLane, so one switch stops both and the monitor shows them as one. SyncLane warns when it sees it (`InstanceInfo.SharesNameWith`, a Degraded health check), but only a name of its own prevents it. A running integration keeps the name it has ([step U3](#step-u3-name-the-application-and-its-roles)).
- Keep one application per queue, and start the name of every new queue with the application's name; never rename a queue that already runs ([section 7](#what-never-changes-in-an-upgrade)). Copies of one application share their queues; a second application that runs the same queue is refused at start-up (`SL1011`). An instance switched off, workers and planner, claims no queue and may call `ISyncLane` and the monitor freely.
- Run one process per container. If a worker and a web host share a container and one of them dies, the orchestrator does not restart it.
- The worker needs no open port. Its state is in the database: the monitor reads it from any instance.
- Work the user waits for (an answer on screen) stays in the web process. Work nobody waits for (fetching, reports, sending data out) becomes a job, and a "refresh" button enqueues it and returns at once.

### D3. Owner, lane and budget key

- **Owner key:** stable and unique per queue, usually the connected account's id. Never a name or an email. Give one account the same key on every queue, so the monitor counts its jobs on all of them together (`GetJobOwnersAsync`).
- **Lane:** leave it out, and each owner gets its own lane. Give owners that share credentials or a rate limit the same lane, so their jobs never run at the same time.
- **Budget key:** usually the owner key. Owners that share one rate limit pass one key.
- **Name:** what people call the owner, shown and searched by the monitor. It may change at any time.
- **Values:** what a recipe needs besides the key, such as a region or a marketplace, as `JobOwner.Values`.

### D4. Where the code lives

Keep everything that belongs to SyncLane in one place, so it can be found, tested and, if need be, removed. Follow the project's own layers and names; this shape works as a folder of a single project or of its infrastructure layer:

```
Sync/
  SyncRegistration.cs          AddShopSync(services, configuration): every AddSyncLane, AddBudget and AddSyncLaneHttp call
  Owners/
    ConnectedShops.cs          IOwnerSource
  Handlers/
    Orders/OrderHandler.cs     one folder per job type
    Products/ProductHandler.cs
  Recipes/
    orders.jsonc               copied to the output
  Credentials/
    ShopCredentials.cs         ICredentialStore over the project's own storage
  Sinks/
    OrderSink.cs               IRecordSink, when a recipe writes to the project's tables
```

- **Handlers are thin.** Fetch one page, call the project's existing services for the rules, write through its existing repositories. No business rule is written twice.
- **In a layered project,** what the domain needs stays in the application layer; handlers, the owner source and the credential store go where the infrastructure lives, because they touch the database and the network.
- **One registration method.** `Program.cs` gets one line, `builder.Services.AddShopSync(builder.Configuration);`, and the end-to-end test of [section 5](#5-prove-it) calls the same method.
- **Recipes travel with the build:** `<None Include="Sync/Recipes/*.jsonc" CopyToOutputDirectory="PreserveNewest" />` in the project file.
- **The old jobs stay** until the cut-over in [section 5](#5-prove-it), and are deleted in a later change.

## 4. Steps

### Step 1. Add the packages

```bash
dotnet add package SyncLane
dotnet add package SyncLane.Http
dotnet add package SyncLane.AspNetCore
```

`SyncLane.Http` is needed only for recipes, `SyncLane.AspNetCore` only for the monitor over HTTP. Their namespaces: `SyncLane` (the engine: `AddSyncLane`, `IJobHandler`, `IOwnerSource`, `ISyncLane`, `ISyncLaneMonitor`), `SyncLane.Http` (`AddSyncLaneHttp`, `HttpRecipeHandler`, `ICredentialStore`, `IRecordSink`) and `SyncLane.AspNetCore` (`MapSyncLane`).

**Done when** the project builds.

### Step 2. Connection and configuration

Add a connection string, and a `SyncLane` section with the application's name and the numbers that change per environment:

```json
{
  "ConnectionStrings": { "Postgres": "Host=db;Database=app;Username=app" },
  "SyncLane": {
    "Application": "shop",
    "Queues": {
      "shop.stores": {
        "Concurrency": 2,
        "Types": { "orders": { "Every": "00:15:00" } }
      }
    }
  }
}
```

`Application` is the name the monitor, the switches and the other applications on the schema know this one by: lower case, short, its own, and the same in every environment. Do not add `RunWorkers` or `RunPlanner`; whether the application works is a switch ([D2](#d2-which-process-does-what)). The password comes from where the project keeps secrets, never from a committed file. Behind PgBouncer in transaction mode, also set `SyncLane:ListenConnectionString` to a connection that goes around the pooler ([Behind a connection pooler](operations.md#behind-a-connection-pooler)). If none can, switch listening off from the monitor before the first deploy, `SetListeningAsync("shop", null, listen: false, reason: "behind the pooler", by: user)`, so the application never tries; its workers then find jobs every `PollInterval`. `SyncLane:ListenForChanges` stays out of the settings, like `RunWorkers` and `RunPlanner`.

**The schema needs no work by hand.** With the defaults, the first start creates the schema `synclane` in the database of `ConnectionString`, with every table and index, behind a lock so instances that start together do not collide; each later SyncLane version adds its migrations the same way. SyncLane never touches the project's own tables. Several applications can share one database and one schema: each declares its own queues, and the monitor of any of them shows every application's jobs ([Several applications on one schema](operations.md#several-applications-on-one-schema)). Keep the default name unless the project's database rules say otherwise (`SyncLane:Schema`, lower case). If the application's database user may not create a schema, run the script from the project's migrations instead ([step 12](#step-12-deploy)).

**Done when** the application starts with the section present and the connection string resolves.

### Step 3. The owner source

One class that returns who is connected now. The planner calls it once a minute.

```csharp
public sealed class ConnectedShops(AppDb db) : IOwnerSource
{
    public async Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
        await db.Shops
            .Where(shop => shop.IsConnected)
            .Select(shop => new JobOwner(shop.Id.ToString(), null, shop.Name)
            {
                Priority = shop.IsNew ? JobPriority.High : JobPriority.Normal
            })
            .ToListAsync(cancellationToken);
}
```

- Inside a query, give `JobOwner` its arguments in order: the key, the lane (`null` for a lane of its own) and the name. A named argument out of order, such as `Name: shop.Name`, does not compile in an expression tree.

- A `High` priority for new accounts makes their history load before the others'.
- Return only owners that should have jobs. An owner left out gets no new jobs; the jobs it already has still run until they are cancelled ([step 10](#step-10-accounts-that-disconnect-or-leave)).
- It is resolved in a scope of its own, so a scoped `DbContext` is fine.

**Done when** it returns the expected owners in a test or a debug run.

### Step 4a. A handler

One page per call. SyncLane saves the returned cursor after every page and calls again until the cursor is `null`.

```csharp
public sealed class OrderHandler(IShopClient client, OrderRepository orders, RateBudgets budgets) : IJobHandler
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

Write the recipe with `synclane init` from a curl that works, check it with `synclane test` (two pages, no database), and keep it in the project, for example `Sync/Recipes/orders.jsonc`. Then a credential store over the project's own storage:

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
    http.Recipe("orders", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sync/Recipes/orders.jsonc")))
        .WriteToPostgres("shop_orders", table => table.VersionedBy("$.updated_at"));
});
```

`WriteToPostgres` creates the table and upserts one row per owner and record. For the project's own tables, implement `IRecordSink` (one method, `WriteAsync(RecordPage page, ...)`), upsert there, and register it with `.WriteTo<OrderSink>()` instead.

**Done when** `synclane test Sync/Recipes/orders.jsonc` reads two pages and the application starts without a recipe error.

### Step 5. Declare the queue

```csharp
builder.Services.AddSyncLane(builder.Configuration.GetSection("SyncLane"), lane =>
{
    lane.ConnectionString = builder.Configuration.GetConnectionString("Postgres")!;
    lane.AddBudget("shop-api", limit: 2, per: TimeSpan.FromSeconds(1), burst: 10);

    lane.AddQueue("shop.stores", queue => queue
        .Owners<ConnectedShops>()
        .Windowed<OrderHandler>("orders", horizon: TimeSpan.FromDays(30), slice: TimeSpan.FromDays(7),
            every: TimeSpan.FromMinutes(15), overlap: TimeSpan.FromMinutes(10))
        .Snapshot<ProductHandler>("products", every: TimeSpan.FromHours(6))
        .Handle<InvoiceHandler>("invoice-upload"));
});
```

- A new queue's name starts with the application's name, so it cannot meet another application's on the schema. A queue that already runs keeps its name.
- `Windowed`: history in slices back to `horizon`, newest first, then one job per `every` from where the data ends.
- `Snapshot`: one job that reads everything, then one per `every`.
- `Handle`: never planned; runs only when enqueued.
- Each job type is a handler ([step 4a](#step-4a-a-handler)) or a recipe ([step 4b](#step-4b-a-recipe)), never both. A recipe's job type is declared with `HttpRecipeHandler` and has the recipe's name: `.Windowed<HttpRecipeHandler>("orders", ...)` for `http.Recipe("orders", ...)`.
- Types run in the order they are declared, within a priority.
- Declare the same budgets in every process that calls the API, the web process included.

**Done when** the application starts, the log shows no `SL1xxx` error, the schema `synclane` exists with its tables, and `synclane.jobs` has rows for the owners from step 3.

### Step 6. Split the roles, if you chose to

For the web and worker layout ([D2](#d2-which-process-does-what)), the same image runs twice, and only the worker has a role:

```yaml
web:
  image: app
worker:
  image: app
  environment:
    SyncLane__Role: "worker"
```

Before the first deploy with the split, set the two switches from the monitor. A switch can be set for an application and role that has not started yet, so neither container works where it should not, even for a moment:

```csharp
await monitor.SetSwitchAsync("shop", role: null, runWorkers: false, runPlanner: false, reason: "web instances stay idle", by: user);
await monitor.SetSwitchAsync("shop", role: "worker", runWorkers: true, runPlanner: true, reason: "the worker syncs", by: user);
```

Over HTTP: `POST /synclane/switch?application=shop&workers=false&planner=false&reason=web%20instances%20stay%20idle`, then `POST /synclane/switch?application=shop&role=worker&workers=true&planner=true&reason=the%20worker%20syncs`. The same call with `false` stops the worker later; its running jobs are handed back with their cursors. A new replica of either reads its switch when it starts, so scaling needs nothing more. See [Switching an application on and off](operations.md#switching-an-application-on-and-off).

The switches live in the database of each environment: set them once in each (test, staging, production). Where none is set, both containers run workers, which is safe for the data but takes CPU from the web instances.

Background services of the project that must run once, not in every web copy, belong to the worker too: register them only in the worker role.

```csharp
if (builder.Configuration["SyncLane:Role"] == "worker")
    builder.Services.AddHostedService<ExpiredDataCleanup>();
```

**Done when** `GetSwitchesAsync()` shows the application's two rows, the worker role with its instances running workers and the planner and the web instances running neither, and jobs complete.

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

It answers only requests from the same machine until it requires authorization. Require it before anything else: the switches stop and start every application on the schema, not only this one. Every action (pause, requeue, skip, sync now) is logged with who and why. The routes are in [the package's readme](../src/SyncLane.AspNetCore/README.md); in code, inject `ISyncLaneMonitor` ([Watching it run](monitoring.md)). A screen of the project's own reads these calls, never SyncLane's tables with SQL of its own, which a SyncLane upgrade may change: each owner's jobs by status, for example, are `GetJobOwnersAsync` (`GET /synclane/jobs/owners?queues=shop.stores`).

**Done when** `GET /synclane/overview` returns the queue with an authorized user, and is refused without one.

### Step 9. Refresh on demand and new accounts

A "refresh" button or a webhook fetches from where the owner's data ends to now, at high priority. A burst of calls is one fetch, and the call returns at once; the worker does the work:

```csharp
await monitor.SyncNowAsync("shop.stores", owner: shop.Id.ToString(), type: "orders", by: user.Id);
```

An account that just connected is planned now instead of at the next round:

```csharp
await syncLane.PlanAsync("shop.stores", new JobOwner(shop.Id.ToString(), Name: shop.Name));
```

"Connecting..." on screen needs no status column of the project's own:

```csharp
var progress = await syncLane.GetProgressAsync("shop.stores", shop.Id.ToString());
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

1. Switch the application off from the monitor (`SetSwitchAsync("shop", null, runWorkers: false, runPlanner: false, …)`), then deploy.
2. Call `syncLane.MarkLoadedAsync(queue, ranges)` with what the old system loaded ([Moving from another system](operations.md#moving-from-another-system)).
3. Stop the old system.
4. Switch the planner and the workers on.

**Done when** the first jobs of a migrated owner cover only what is older than its marked range and what came after.

### Step 12. Deploy

- **Schema:** with `CreateSchema = true` the first instance creates it and applies migrations behind a lock. With the project's own migrations, add `migrationBuilder.Sql(SyncLaneSchema.Script("synclane"))` now, and again in a new migration with each SyncLane upgrade that adds one ([Running the schema yourself](operations.md#running-the-schema-yourself)).
- **Rolling deploys** are safe: migrations only add, and an older instance keeps working against the newer schema.
- **Clocks:** keep the hosts' clocks in sync; leases and budgets use them.
- **Connections:** each instance keeps one connection open to hear job changes, outside any pooler, from when its workers first wait for a job or something watches (a web instance whose workers are off opens none unless a live view reads it); and it takes connections from its pool only while a statement runs, rarely more than its busy slots plus a few for the planner, the heartbeat and the monitor. With many applications and replicas on one PostgreSQL, add them up, with the applications' own pools, against `max_connections`; cap SyncLane's pool with `Maximum Pool Size` in its connection string if you must, switch off what does not need to work, and switch off listening (`SetListeningAsync`) where polling every `PollInterval` is fast enough: that frees one connection per instance.

**Done when** two copies of the worker run side by side for a day with every job created once and never two running jobs in one lane.

## 5. Prove it

No page can promise that an integration works. These gates are how you know it does; the integration is done when every one of them has passed, with the evidence in the pull request.

### Tests

- **The project's existing tests.** Run the whole suite before you start and keep the result. After the integration the same tests pass, unchanged.
- **A unit test per handler:** the four cases of [step 4a](#step-4a-a-handler), and the rules from the inventory: the same input gives the same stored result as the old code did.
- **One end-to-end test against a real PostgreSQL.** It runs the project's own registration with a fake API client and checks that the records arrive. A schema of its own per run keeps it apart from everything else. `WaitUntilAsync`, `CountOrdersAsync` and `DropSchemaAsync` are small helpers of the test project; `TestDatabase.ConnectionString` reads an environment variable, never a literal; `AddShopSync` is the same registration `Program.cs` calls; and the test database holds the shops the owner source returns:

```csharp
[Fact]
public async Task A_connected_shops_orders_reach_the_orders_table()
{
    var schema = $"t_{Guid.NewGuid():N}";
    var builder = Host.CreateApplicationBuilder();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = TestDatabase.ConnectionString,
        ["SyncLane:Schema"] = schema,
        ["SyncLane:PlanInterval"] = "00:00:01",
        ["SyncLane:PollInterval"] = "00:00:01",
    });
    builder.Services.AddShopSync(builder.Configuration);
    builder.Services.AddSingleton<IShopClient>(new FakeShopClient(ordersPerPage: 100, pages: 3));
    using var host = builder.Build();
    await host.StartAsync();

    var syncLane = host.Services.GetRequiredService<ISyncLane>();
    await WaitUntilAsync(async () =>
    {
        var all = await syncLane.GetJobsAsync(new JobQuery { Queue = "shop.stores", IncludeTotal = true });
        var done = await syncLane.GetJobsAsync(new JobQuery { Queue = "shop.stores", Status = JobStatus.Completed, IncludeTotal = true });
        return all.Total > 0 && done.Total == all.Total;
    }, timeout: TimeSpan.FromMinutes(1));

    Assert.Equal(300, await CountOrdersAsync(shopId: TestShops.First));
    await host.StopAsync();
    await DropSchemaAsync(schema);
}
```

- **SyncLane's own tests** (the lease, planning, rate budgets, crash recovery, on PostgreSQL 14 to 18 and through PgBouncer) run in SyncLane's repository for every release. The project does not copy them; it tests its use of SyncLane.

### The same data as before

Run the new path beside the old one, as the [production trial](production-trial.md) describes: the old jobs write where they always did, the new handlers into a copy of the tables (or a staging database) for the same accounts. Then compare per account and day, both ways:

```sql
SELECT shop_id, date_trunc('day', ordered_at) AS day, count(*) AS orders,
       md5(string_agg(external_id || ':' || net_total::text || ':' || status, ',' ORDER BY external_id)) AS fingerprint
FROM orders GROUP BY 1, 2
EXCEPT
SELECT shop_id, date_trunc('day', ordered_at), count(*),
       md5(string_agg(external_id || ':' || net_total::text || ':' || status, ',' ORDER BY external_id))
FROM orders_new GROUP BY 1, 2;
```

Run it again with the two tables swapped. Every row returned is a difference to explain before the cut-over. Count the side effects the same way: events published and emails sent per day, old and new.

### The first run

```sql
SELECT type, status, count(*) FROM synclane.jobs GROUP BY type, status ORDER BY type, status;
```

- Jobs move from `pending` to `running` to `completed`; recent slices complete first.
- `GetProblemsAsync()` is empty, or groups the failures by error code; [Error codes](errors.md) says what each means and how to fix it.
- The project's tables fill, and running the same window again changes no row count.
- `GetBudgetsAsync(new BudgetQuery())` shows each budget's rate; frequent `429`s mean the declared limit is too high.

### Done, and the cut-over

The integration is done when all of these hold:

- [ ] The build passes with no new warnings.
- [ ] The existing tests pass, unchanged; the new unit tests and the end-to-end test pass.
- [ ] The comparison returns no unexplained difference for 7 days in a row, side effects included.
- [ ] `/health` is `Healthy`; no job is `abandoned` without a known cause.
- [ ] The drills of the production trial behaved as expected.

Then turn the old jobs off, keep their code for one more release in case you must turn them back on, and delete it in a later change.

## 6. Before production

Run the [production trial](production-trial.md): one integration that is allowed to fail, slow settings, the old path beside it for two weeks, and the drills (graceful stop, `kill -9`, rolling deploy, two instances, provider outage, revoked access).

## 7. Upgrading an existing integration

For a project that already runs SyncLane. The order matters more here than anywhere else on this page: the integration is live, and a change made in the wrong order shows at once in production. Read the project's SyncLane code and settings first, as [section 1](#1-what-the-project-does-today) reads its jobs, and write down what you find.

### What never changes in an upgrade

| Do not change | Because |
| --- | --- |
| A queue's name | A queue of another name is a new queue: its owners' history is fetched again from the horizon, and the jobs of the old one wait with no worker. The rule that queue names start with the application's name is for new queues only. |
| A job type's name | Each type keeps what it has loaded under its name; a new name loads it all again. |
| An owner's key | The same: the owner's history loads again under the new key. |
| A budget's name or key, without a reason | Its learned rate starts again from its declared limit. |

### Step U1. Find what the project has

List, for every environment (test, staging, production) and every kind of container (web, worker):

- the SyncLane packages and their version;
- `Application`, `Role`, `RunWorkers`, `RunPlanner` and `ListenForChanges`, from `appsettings*.json`, from environment variables (`SyncLane__RunWorkers`, ...) and from the deployment files, with the value each kind of container ends up with;
- the queues, job types and budgets the code declares.

**Done when** the pull request has the table, one row per environment and kind of container.

### Step U2. Upgrade the packages

Raise every SyncLane package to the same version. Read each version's **Changed** section in `CHANGELOG.md` between the old version and the new one: it lists what a project may have to change. The schema upgrades itself at start-up (with the project's own migrations, add `migrationBuilder.Sql(SyncLaneSchema.Script("synclane"))` in a new migration, [step 12](#step-12-deploy)). Change nothing else in this step.

**Done when** the project builds, its tests pass unchanged, and the new version runs in test with the old settings, doing what it did before.

### Step U3. Name the application and its roles

- **The name.** Set `SyncLane:Application` to the name the instances already register under (`GetInstancesAsync`), so that it is written down and nothing else changes. Choose a new one only when that name is shared with another application (`SharesNameWith`). To change it, switch the old name off first, `SetSwitchAsync(oldName, null, runWorkers: false, runPlanner: false, ...)` for each of its roles, then deploy the new name: instances that are switched off claim no queue, so the new ones start. Without that, a new instance that starts while one under the old name still runs the same queues is refused (`SL1011`), and a rolling deploy waits for it forever.
- **The role.** Where one build runs as web and worker, add `SyncLane__Role=worker` to the worker's environment, and keep the old settings in place for now.

**Done when** `GetInstancesAsync` shows every instance under the name and role you expect, all of them following switches (`FollowsSwitches`), and `GetSwitchesAsync` shows no instance ignoring them (`IgnoringSwitch` is 0).

### Step U4. Write the settings into switches, then remove the settings

The settings still decide what each container does. Write the same decisions into switches, in each environment, before any setting is removed. A switch with the same values as the settings changes nothing at the moment it is set, so this step is safe while the old settings are still deployed:

```csharp
await monitor.SetSwitchAsync("shop", role: null, runWorkers: false, runPlanner: false, reason: "web instances, as their settings said", by: user);
await monitor.SetSwitchAsync("shop", role: "worker", runWorkers: true, runPlanner: true, reason: "the worker, as its settings said", by: user);
await monitor.SetListeningAsync("shop", role: null, listen: false, reason: "behind the pooler, as its settings said", by: user);
```

Only for what the table of step U1 shows: a container whose settings leave `RunWorkers`, `RunPlanner` or `ListenForChanges` at `true` needs no switch for it. Over HTTP: `POST /synclane/switch` and `POST /synclane/switch/listen`, as in [step 6](#step-6-split-the-roles-if-you-chose-to). The switches are per environment; an AI assistant in the code cannot set them, and should not: it writes them into the pull request as a list for the people who run each environment, and the settings are removed only after that list is done.

Then, in a deploy of its own, remove `RunWorkers`, `RunPlanner` and `ListenForChanges` from `appsettings*.json`, the environment variables and the deployment files.

**Done when** `GetSwitchesAsync` shows, in every environment, each role running what it ran before the upgrade, and the settings hold none of the three.

### Step U5. Prove nothing changed

The gates of [section 5](#5-prove-it), shorter: the project's tests pass unchanged; for a day after each step, the jobs created and completed per type, and the records written, match the days before; no job is `abandoned` without a known cause; `/health` is `Healthy`.

## Mistakes we have seen

| Mistake | What happens | Instead |
| --- | --- | --- |
| An owner key that can change, such as a name | The owner's history loads again under the new key. | A stable id; the name goes in `JobOwner.Name`. |
| Inserting records | A page that runs twice duplicates them. | Upsert by the record's id. |
| A slice that takes hours for the biggest owner | A failure late in the slice repeats most of it. | A slice that finishes in minutes at the API's rate. |
| Looping over all pages in one handler call | Nothing is saved until the end; a crash starts over. | Return after one page. |
| A budget declared only in the worker | The web process's calls do not count against the limit. | The same `AddBudget` in every process that calls the API. |
| Two applications running one queue | The second one is refused at start-up (`SL1011`). | One application per queue, its name starting with the application's. |
| Two applications under the default name (`Api`) | One switch stops both; the monitor shows them as one; SyncLane warns (`SharesNameWith`, Degraded). | `SyncLane:Application`, a name of its own, in each. |
| `RunWorkers: false` in `appsettings.json` to keep web instances idle | The decision is spread over every project and needs a deploy to change. | Leave it out; switch the web instances off from the monitor. |
| `RunPlanner` off in every process | No jobs are created. | At least one process plans; `GetOverviewAsync` shows `Planners` per queue, and `GetSwitchesAsync` what each role runs. |
| Renaming a queue or a job type in a running integration | The history of every owner is fetched again, and the old queue's jobs wait with no worker. | Keep the names; new names only for new queues ([section 7](#what-never-changes-in-an-upgrade)). |
| Removing `RunWorkers: false` before the switches are set | The web instances start working at the next deploy. | Switches first, in every environment, then remove the setting ([step U4](#step-u4-write-the-settings-into-switches-then-remove-the-settings)). |
| Renaming the application in a rolling deploy | The new instances are refused (`SL1011`) while the old ones run the queues, and the rollout never finishes. | Switch the old name off first ([step U3](#step-u3-name-the-application-and-its-roles)). |
| Many applications and replicas on one PostgreSQL, connections never counted | `too many clients` when the last replica starts. | Count each instance's listening connection and pool against `max_connections` ([step 12](#step-12-deploy)). |
| A job type declared with its own handler and also given a recipe | The application does not start (`SL5015`). | One or the other per job type; a recipe's type uses `HttpRecipeHandler`. |
| Listening through PgBouncer in transaction mode | Live changes never arrive, and other clients see timeouts. | `ListenConnectionString` around the pooler, or listening switched off from the monitor (`SetListeningAsync`). |
| Deleting job rows by hand | A returning owner never loads its history again. | `ForgetOwnerAsync`. |
| Following this page over the project's own `CLAUDE.md` or `ARCHITECTURE.md` | The integration breaks the project's conventions. | The project's rules first; this page inside them. |
| Copying a business rule into the handler | Two copies drift apart; the old and new data stop matching. | Call the service that holds the rule today. |
| Editing an existing test so it passes | A change in behaviour goes unnoticed. | Find what the integration changed, and fix that. |
| A token in a recipe, a test or `appsettings.json` | A secret in the repository. | `ICredentialStore`, or an environment variable while testing. |

## Checklist

- [ ] The project's `CLAUDE.md`, `ARCHITECTURE.md` and similar rules are followed; every place this page gave way to them is listed in the pull request.
- [ ] The code adds no comments, except those the project's rules require.
- [ ] Today's behaviour is written down, one row per job, with the decision for each.
- [ ] The questions in section 2 are answered, or their defaults written down.
- [ ] SyncLane's code lives in one place, registered by one method.
- [ ] Every business rule is called from where it lived before, not copied.
- [ ] Owner keys are stable; owners that share a limit share a lane and a budget key.
- [ ] Every write is an upsert; a page run twice changes nothing.
- [ ] Each handler returns after one page, uses the job's window and cursor, and throws the right exception for `429` and revoked access.
- [ ] Budgets are declared in every process that calls the API.
- [ ] No credential in code, recipes, configuration files, tests or cursors.
- [ ] `SyncLane:Application` is set to a name of its own, and every new queue's name starts with it; in an upgrade, the names it ran under are kept ([section 7](#7-upgrading-an-existing-integration)).
- [ ] `RunWorkers`, `RunPlanner` and `ListenForChanges` are not in the settings; roles are set where one build runs as web and worker, and their switches are set in each environment; one process per container; at least one process plans.
- [ ] Health check, telemetry and alerts are wired.
- [ ] The monitor requires authorization.
- [ ] Disconnect and removal call `CancelAsync` and `ForgetOwnerAsync`.
- [ ] The existing tests pass unchanged; the handler tests and the end-to-end test pass.
- [ ] The old and new data match for 7 days, side effects included.
- [ ] The production trial has started.
