<p align="center"><img src="assets/icon.png" width="112" alt="SyncLane"></p>

# SyncLane

[![CI](https://github.com/ferhatblnk/synclane/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ferhatblnk/synclane/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
[![Tested on PostgreSQL 14 to 18](https://img.shields.io/badge/tested%20on-PostgreSQL%2014%E2%80%9318-336791.svg)](.github/workflows/ci.yml)

**Sync jobs against other people's APIs, from one PostgreSQL table you own.** Every job is a row: its window, its cursor, its attempts, its error.

If your application pulls orders, products or anything else from an external API for many accounts, it needs the same things every time: load two years of history in slices, pick up what is new every few minutes, continue after a crash instead of starting over, back off when the other side says "slow down", never hit one account's limit from two workers at once, and send data back in order. SyncLane is a .NET library that does exactly that, inside your process, with no dependency beyond Npgsql and the `Microsoft.Extensions` abstractions.

## Why

Hangfire, Quartz and the like are good at "run this method at that time". A sync job is a different animal. It has a range of time it covers, a position inside that range, and a neighbour it must not overlap with. When you put that on top of a general scheduler, the interesting state ends up somewhere you cannot see: in serialized arguments, in a second table of your own, or in a lock.

SyncLane keeps that state where you can read and change it:

- **A window on every job.** History is cut into slices; each slice is a job with a start and an end. What is new gets a window that starts where the last one ended.
- **A cursor on every job.** It is saved after each page. A job that fails, is redeployed or loses its worker continues from its last page.
- **Lanes.** Jobs of one account run one after the other; accounts run side by side.
- **Rate budgets.** Requests to one account go out at the pace you declare, shared by every job and queue that calls it. When the API says "slow down", every job on that account waits, not only the one that heard it.
- **Planning that can be repeated.** The planner compares what should exist with what exists and adds the difference. Calling it a thousand times changes nothing. Raising the history from 60 days to two years fills only the gap.
- **A row you can act on.** `status`, `attempt_count`, `error`, `next_run_at`, `leased_by`. If something is stuck, `SELECT` tells you why and `UPDATE` fixes it.

## Quick start

```bash
dotnet add package SyncLane
dotnet add package SyncLane.Http             # recipes instead of handlers
dotnet tool install --global SyncLane.Cli    # synclane init, test and mcp
```

### With a recipe

For an API that lists JSON records page by page, you write no request code. Give your AI assistant `synclane mcp` and a curl that works, and it writes the recipe, checks it and runs it for two pages ([docs/ai.md](docs/ai.md)); or run `synclane init` and `synclane test` yourself ([docs/recipes.md](docs/recipes.md#starting-from-a-curl)). Then:

```csharp
builder.Services.AddSyncLane(lane =>
{
    lane.ConnectionString = builder.Configuration.GetConnectionString("Postgres")!;
    lane.AddQueue("shops", queue =>
    {
        queue.Owners<ConnectedShops>();
        queue.Windowed<HttpRecipeHandler>("orders", horizon: TimeSpan.FromDays(365), slice: TimeSpan.FromDays(7), every: TimeSpan.FromMinutes(15));
    });
});

builder.Services.AddSyncLaneHttp(http =>
{
    http.Credentials<ShopCredentials>();   // your ICredentialStore: credentials stay in your own storage
    http.Recipe("orders", File.ReadAllText("recipes/orders.jsonc"))
        .WriteToPostgres("shop_orders", table => table.VersionedBy("$.updated_at"));
});
```

`WriteToPostgres` creates the table and keeps one row per account and record; a page that runs twice changes nothing. Your own tables take an `IRecordSink`. Paging, OAuth 2.0 and other auth, rate limits, retries and resuming are the recipe's and SyncLane's job.

### With a handler

For anything a recipe does not cover (multi-step reports, GraphQL, your own client), a handler does one page and says where the next one starts:

```csharp
lane.AddQueue("shops", queue =>
{
    queue.Owners<ConnectedShops>();
    queue.Windowed<OrderHandler>("orders", horizon: TimeSpan.FromDays(730), slice: TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15));
});

public sealed class OrderHandler(ShopApi api, OrderStore store) : IJobHandler
{
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        var page = await api.GetOrdersAsync(job.Owner, job.WindowStart, job.WindowEnd, job.Cursor, cancellationToken);
        await store.UpsertAsync(page.Orders, cancellationToken);
        return new JobPage(page.NextCursor, page.Orders.Count);
    }
}
```

### Either way, an owner source

It tells the planner who is connected. The key never changes; the lane groups owners that share a rate limit; the name is what the monitor shows and searches.

```csharp
public sealed class ConnectedShops(AppDb db) : IOwnerSource
{
    public async Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
        await db.Shops.Where(shop => shop.IsConnected)
            .Select(shop => new JobOwner(shop.Id.ToString(), shop.SellerId, shop.Name))
            .ToListAsync(cancellationToken);
}
```

That is the whole integration. The table is created at start-up, the planner runs once a minute and the workers start pulling. Every other setting has a default you can leave alone ([docs/configuration.md](docs/configuration.md)).

## What do I use for...

### Getting data sooner

| I want | Use |
| --- | --- |
| An account's new data now, for a webhook or a "refresh" button | `monitor.SyncNowAsync(queue, owner, type)`: a burst of calls is one fetch |
| One job to run before everything else | `NewJob.Priority = JobPriority.Urgent` |
| A new account's history to load before the others | `Priority = JobPriority.High` on its `JobOwner`, in the owner source |
| An account's waiting jobs moved forward once, by an operator | `monitor.PrioritizeOwnerAsync(queue, owner)` |
| An account planned the moment it connects, not at the next round | `syncLane.PlanAsync(queue, owner)` |

### Stopping and repairing

| I want | Use |
| --- | --- |
| A queue or an account stopped at once, for a while | `monitor.PauseAsync(queue, owner, reason, until: ...)`, then `ResumeAsync` |
| No more jobs for an account that disconnected | `syncLane.CancelAsync(queue, owner, reason)`, and leave it out of the owner source |
| Everything about an account gone for good | `syncLane.ForgetOwnerAsync(queue, owner)` |
| One failed job tried again, from where it stopped | `monitor.RequeueJobAsync(id)`; every job with one error code: `RequeueByCodeAsync` |
| A window fetched again from its first page | `monitor.RestartJobAsync(id)` |
| One job never to run, so the jobs behind it go on | `monitor.SkipJobAsync(id, reason)` |
| An API called more gently for a while | `monitor.LimitBudgetAsync(budget, key, limit, per, reason, until: ...)` |

Every operator call is logged with who and why, and is also on HTTP with `SyncLane.AspNetCore` ([docs/monitoring.md](docs/monitoring.md)).

### Sending data out

| I want | Use |
| --- | --- |
| Each delivery sent once, even if asked twice | `NewJob.DedupKey`, such as `$"movement:{id}"` |
| One account's deliveries in order, a failed one holding the rest | `queue.Ordered = true` ([ordered queues](docs/operations.md#ordered-queues)) |
| The handler and the monitor to know who asked | `NewJob.RequestedBy` |

## What it promises

- **A page is handled at least once**, not exactly once. A crash between your write and the cursor save makes the page run again. Write with upserts.
- **One job per lane at a time**, across every process that shares the table.
- **A planned job is created once**, however many instances plan and however often.
- **Nothing is lost on shutdown or crash.** The cursor on the row is where the job continues.

## Limitations

- **PostgreSQL only.** The lease is a single statement that relies on `FOR UPDATE SKIP LOCKED` and partial unique indexes.
- **No job arguments yet.** A job knows its owner (with its values), type, window, dedup key and who asked; anything else the handler looks up.
- **Periods are aligned to the clock.** "Every day" means once per UTC day.
- **No dashboard.** The monitor is an API, and HTTP endpoints with `SyncLane.AspNetCore`; the screens are yours.

## Read on

| | |
| --- | --- |
| [How it works](docs/how-it-works.md) | The table, a job's life, the statements and the indexes behind the guarantees |
| [Planning](docs/planning.md) | History, slices, periods and overlap, and why |
| [Recipes](docs/recipes.md) | Every part of a recipe; [with an AI assistant](docs/ai.md) |
| [Working with jobs](docs/jobs.md) | Every call, results and errors, priorities, rate budgets in a handler |
| [Watching it run](docs/monitoring.md) | The monitor, live changes, HTTP, metrics, health checks |
| [Configuration](docs/configuration.md) | Every setting, and settings per environment |
| [Operations](docs/operations.md) | Repairs, ordered queues, poolers, several instances and applications, the schema |
| [Error codes](docs/errors.md) | Every code with its cause and fix |
| [Production trial](docs/production-trial.md) | The checklist for a first run on something that may fail |
| [The demo](docs/demo.md) | Four pretend shop accounts, with retries and a broken page, in Docker |

## Project status

Version 0.5. Before 1.0 a minor version may still change the API; every change is listed in [CHANGELOG.md](CHANGELOG.md). The design comes from a job system that has synced external APIs for many accounts in production; this repository is a clean, general rewrite with its own tests, run on PostgreSQL 14 to 18 and through PgBouncer, but this code has not yet run in production itself. Try it on something that is allowed to fail first.

## Development

```bash
dotnet build
SYNCLANE_TEST_DB="Host=localhost;Username=postgres;Database=synclane_test" dotnet test
```

Without `SYNCLANE_TEST_DB` the PostgreSQL tests are skipped. Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Your responsibility

SyncLane is a library. It runs inside your process, with the credentials you give it, against the database and the destinations you choose. It sends nothing to the SyncLane authors: there is no telemetry, no license check that calls home and no hosted service in between. Your data never passes through anyone but you.

That also means the decisions are yours. When you use SyncLane, you are responsible for:

- **Your right to call the API.** Having an account or app registration with the provider, accepting its terms, and staying within the limits and uses it allows.
- **The pace you choose.** Concurrency, windows, retries and delays are settings. SyncLane applies them; it does not know what a provider tolerates. Start slow on an account that matters.
- **Your credentials.** Where they are stored, who can read them and when they are rotated. Recipes ask your `ICredentialStore` for them page by page; SyncLane stores none.
- **The data you pull and where you write it.** Including personal data and the laws that apply to it where you and your users are, such as the GDPR or Turkey's KVKK: what you keep, for how long and who may see it.
- **What your handlers do.** SyncLane calls your code; it does not review it.

Anything in this repository that describes a third-party API, including the demo and any example, is an illustration written by contributors. It is not a statement by the provider and may be out of date. Check the provider's own documentation before you rely on it.

## License

[Apache License 2.0](LICENSE). The software is provided on an "AS IS" basis, without warranties or conditions of any kind, and no contributor is liable for damages arising from its use; sections 7 and 8 of the license state this in full.

SyncLane is free to use, with no registration, account or paid edition, and it calls no host of its own: no telemetry, no license check, no update check.

SyncLane is an independent open-source project. It is not affiliated with, endorsed by or supported by the PostgreSQL project or any API provider.
