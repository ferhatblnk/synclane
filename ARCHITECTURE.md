# Architecture

This page explains how SyncLane is built and why. [CLAUDE.md](CLAUDE.md) turns it into rules; [docs/how-it-works.md](docs/how-it-works.md) walks through the SQL behind each step.

## What SyncLane is, and is not

SyncLane is a library that syncs data from other people's APIs on behalf of many accounts. It cuts history into windows, runs each window as a job page by page, saves the cursor after every page, keeps one job per lane running at a time, and retries with backoff. All state lives in one PostgreSQL table that the user owns.

It is a developer kit, not a service. It runs in the user's process, with the user's credentials, against the user's database, and sends nothing anywhere else. It does not transform data, does not host anything, and does not promise exactly-once delivery.

## Priorities

When two goals conflict, the higher one wins:

1. **Job state is never lost or doubled.** A cursor never goes backwards, a planned job exists once, a lane never runs two jobs. These are enforced by the database, not by code paths.
2. **Security.** Nothing leaves the user's infrastructure. Secrets, headers, bodies and cursors stay out of logs, traces and messages.
3. **Performance.** One round trip per operation, indexes for every hot query, no cost for unused features.
4. **Ease of use.** Few concepts, one line to wire up, errors that say how to fix them.

Ease of use is last on purpose: an easy API that loses a cursor is worse than a slightly longer one that does not.

## Components

```mermaid
flowchart TB
    subgraph Public["Public API"]
        client["ISyncLane<br/>(SyncLaneClient)"]
        options["SyncLaneOptions<br/>QueueOptions"]
        budgets["RateBudgets"]
        schema["SyncLaneSchema"]
        results["SyncResult / SyncListResult<br/>SyncError / SyncErrorCodes"]
    end

    subgraph Hosted["Hosted services"]
        init["SchemaInitializer / SchemaCheck"]
        plannerSvc["PlannerService"]
        worker["JobWorker"]
    end

    subgraph Core["Internals"]
        planner["QueuePlanner<br/>JobPlanner (pure)"]
        runner["JobRunner<br/>FailurePolicy (pure)"]
        store["JobStore"]
        sql["JobSql<br/>Migrations"]
        errors["SyncLaneErrors<br/>(message catalog)"]
        telemetry["SyncLaneMetrics<br/>ActivitySource"]
    end

    user["User code<br/>IJobHandler, IOwnerSource"]
    db[("PostgreSQL<br/>synclane.jobs<br/>synclane.migrations")]

    client --> planner
    client --> store
    plannerSvc --> user
    plannerSvc --> planner
    worker --> store
    worker --> runner
    runner --> user
    runner --> telemetry
    user --> budgets
    planner --> store
    init --> store
    store --> sql
    store --> db
    client --> errors
    runner --> errors
    options --> errors
```

| Component | Responsibility | Changes when |
| --- | --- | --- |
| `SyncLaneOptions`, `QueueOptions` | Declare queues and job types; validate at start-up. | A new setting is added. |
| `JobPlanner` | Pure function: given what exists, which jobs are missing. | The planning rule changes. |
| `QueuePlanner` | One read for coverage, one insert for what is missing. | Planning needs more data. |
| `PlannerService` | Calls the owner sources and plans every queue each interval. | Scheduling of planning changes. |
| `JobWorker` | Runs `Concurrency` slots per queue; each slot leases and runs jobs. | Slot scheduling changes. |
| `JobRunner` | Runs one leased job: pages, checkpoints, heartbeat, outcome. | The page loop changes. |
| `FailurePolicy` | Pure function: failed attempt to next status and delay. | Retry behaviour changes. |
| `RateBudgets` | One token bucket per budget and key, shared by every instance: requests wait for tokens, a `429` pauses the bucket. | Budgets read limits from headers (part 2.3), or learn them (2.4). |
| `JobSql`, `BudgetSql` | Every SQL statement, built once for the configured schema. | A query changes. |
| `JobStore` | Executes job statements; every write to a held job is conditional on still holding it. Owns the connection pool. | Table access changes. |
| `BudgetStore` | Executes bucket statements, each one round trip that locks the bucket's row. | Bucket access changes. |
| `Migrations` | The schema as an append-only list of idempotent steps. | The table changes. |
| `SyncLaneErrors` | Every error message, with its code and kind. | An error is added or reworded. |
| `SyncLaneMetrics`, `SyncLaneTelemetry` | Metrics and spans through `System.Diagnostics`. | A signal is added. |
| `SyncLaneClient` | `ISyncLane`: validates input, returns results. | The public API changes. |

### Dependency direction

Dependencies point inwards and downwards. A lower row never references a higher one.

| Layer | May use |
| --- | --- |
| Public API (`ISyncLane`, options, results, errors) | Planning, Storage, Errors |
| Hosted services | Planning, Running, Storage, Telemetry |
| Running | Storage, Errors, Telemetry, user handlers |
| Planning | Storage |
| Storage | Npgsql, Errors |
| Errors, Results, Telemetry | BCL only |

User code reaches SyncLane through two small interfaces and one facade: `IJobHandler`, `IOwnerSource` and `ISyncLane`.

`SyncLane.Http` sits on top and uses only the engine's public API, the same way user code does. A recipe is compiled once when it is loaded (`CompiledRecipe`); building the request of a page (`RequestRenderer`) does no parsing and checks only the values that change per job. Placeholder values are escaped for where they land, and a recipe's host is fixed text, so a recipe can only call the host it names.

## Data model

Three tables in the configured schema.

`jobs` holds one row per job: what it covers (`queue`, `owner`, `lane`, `type`, `mode`, `window_start`, `window_end`, `dedup_key`), where it is (`status`, `cursor`, `processed_count`, `attempt_count`, `error`, `next_run_at`) and who holds it (`leased_by`, `lease_expires_at`, `heartbeat_at`). The full definition is in [docs/schema.sql](docs/schema.sql).

`migrations` records which schema steps the database has.

`rate_buckets` holds one row per rate budget and key: the tokens left, when they were last refilled (in the future while the bucket is paused), how many pauses it has had (`generation`), and how many tokens it has given (`taken`). Its primary key `(budget, key)` serves every statement on it.

The guarantees are indexes, not code:

| Index | Guarantee |
| --- | --- |
| `UNIQUE (queue, owner, type, dedup_key)` | A planned job is created once, however many instances plan at once. |
| `UNIQUE (queue, lane) WHERE status = 'running'` | One running job per lane, across every process. |
| `(queue, next_run_at) WHERE status IN (...)` | Finding the next due job stays cheap as completed rows pile up. |

Every write a worker makes to a job carries `WHERE id = @id AND status = 'running' AND leased_by = @worker`. A worker whose lease was taken over cannot overwrite the new holder; its writes match no row and it stops.

## Concurrency model

- **Slots.** Each process runs `Concurrency` slots per queue. A slot takes one job, runs it to an end and takes the next.
- **Leases.** Taking a job is one `UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED)`. A heartbeat renews the lease every third of `LeaseDuration`. A dead worker's job is taken over once its lease expires.
- **Lanes.** A slot never takes a job whose lane already has one running. Two slots racing for one lane both try; the unique index lets one through and the other retries.
- **Pages.** Pages of one job run in sequence because each starts from the previous cursor. That is the only ordering SyncLane imposes inside a job.
- **Planning.** Every interval, all queues are planned at the same time. Planning from several instances at once is safe because the insert ignores conflicts.
- **Awaiting in loops.** Independent work is batched into one statement or awaited together. A loop awaits in sequence only when order is the point, and says so in a `// Sequential:` comment. `ConventionTests` checks this.

## Error model

Every failure belongs to one category, and the category decides the mechanism:

| Category | Mechanism | Who handles it |
| --- | --- | --- |
| Expected outcome of a call (unknown queue, duplicate, bad limit) | Failed `SyncResult` with a `SyncError` | The caller, by branching on `Succeeded` or the code |
| Configuration (missing setting, missing migration) | `SyncLaneException` at start-up | The developer, before the application serves anything |
| Caller bug (`null` argument) | `ArgumentNullException` from BCL helpers | The developer |
| Infrastructure (database, network) | The original exception propagates | The caller; inside a worker, the retry policy |
| Handler failure | Stored in the job's `error` column | The retry policy, then a person via requeue |
| Cancellation | `OperationCanceledException` | Nobody; it is not a failure |
| Impossible state | `SyncLaneException` with kind `Internal` | A bug report |

The point of the split: a failed result always means "the call was understood and the answer is no". It never means "something broke". Callers can handle every failed result without fearing they are hiding an outage.

### Codes and the catalog

Every SyncLane error has a stable code, `SL` plus four digits. The first digit is the area: 1 configuration, 2 schema and stored data, 3 calls to `ISyncLane`, 4 running jobs, 5 the HTTP source. Each package lists its codes in one class (`SyncErrorCodes`, `HttpErrorCodes`), and every code is documented with cause and fix in [docs/errors.md](docs/errors.md).

All messages of a package are written in one place: `SyncLaneErrors` in the engine, `HttpErrors` in the HTTP source. One catalog means:

- the same problem is always worded the same way, in exceptions, results, logs and the `error` column;
- every message follows the same form: what went wrong, then how to fix it;
- a reviewer can read every error the library can produce in one file;
- applications can translate by code: the code is the localization key, the English message is the fallback.

`SyncError.ToString()` is `"{Code}: {Message}"`, and that is the form used everywhere text is needed.

`SyncErrorKind` says what kind of problem it is, and `ToHttpStatusCode()` gives the status an API built on SyncLane would usually answer with:

| Kind | HTTP |
| --- | --- |
| `Validation` | 400 |
| `Unauthorized` | 401 |
| `NotFound` | 404 |
| `Conflict` | 409 |
| `Throttled` | 429 |
| `Unavailable` | 503 |
| `Configuration`, `Internal` | 500 |

## Result model

```csharp
public class SyncResult                      // Succeeded, Errors, Error, ThrowIfFailed()
public sealed class SyncResult<T>     : SyncResult   // + Value, GetValueOrThrow()
public sealed class SyncListResult<T> : SyncResult   // + Items, NextCursor, HasMore, Total
```

A typical call:

```csharp
var result = await syncLane.EnqueueAsync(new NewJob { Queue = "shops", Owner = owner, Type = "orders", DedupKey = "resync-2026-10" });

if (!result.Succeeded)
    return result.Error!.Code == SyncErrorCodes.JobAlreadyExists
        ? Results.Ok("Already queued")
        : Results.Problem(result.Error.Message, statusCode: result.Error.Kind.ToHttpStatusCode());

return Results.Ok(result.Value);
```

Choices, and why:

- **Typed errors, not strings.** Callers branch on `Code` and `Kind`; a list of strings can only be shown.
- **Immutable.** Results are created by `Success` and `Failure` and never change, so they are safe to cache and share between threads.
- **`Value` and `Items`, not `Result`.** `result.Result` reads badly and is easily confused with `Task.Result`.
- **Own names, prefixed with `Sync`.** Applications often have their own `Result` or `ApiResult` type, frequently from a shared in-house package. A SyncLane type with the same name would make every file that imports both ambiguous.
- **Cursor pages.** `SyncListResult<T>` carries `NextCursor` instead of a page number, because an offset gets slower the deeper it goes and skips or repeats rows when new ones arrive. `Total` is only filled when asked for, because counting a large table is not free.
- **No dependency on in-house packages.** SyncLane is public and builds on its own. An application that answers with `ApiResult<T>` maps in its own code, or in an adapter package kept in a separate solution:

```csharp
// In the application or a separate adapter solution, never in SyncLane.
static ApiResult<T> ToApiResult<T>(this SyncResult<T> result) =>
    result.Succeeded
        ? ApiResult<T>.Success(result.Value!)
        : ApiResult<T>.Failure(result.Errors.Select(error => error.ToString()));
```

## Performance model

Round trips per operation:

| Operation | Round trips | Served by |
| --- | --- | --- |
| Take a job | 1 (up to 3 when racing for a lane) | `jobs_due`, `jobs_one_running_per_lane` |
| Save a page | 1 | Primary key |
| Heartbeat | 1 per third of `LeaseDuration` | Primary key |
| Finish a job | 1 | Primary key |
| Plan a queue | 2: coverage read, batched insert | `jobs_dedup` |
| `EnqueueAsync` | 1 | `jobs_dedup` |
| `GetJobsAsync` | 1, with the optional count in the same batch | Primary key, scanned backwards |
| `RequeueAsync`, `CancelAsync`, `GetProgressAsync` | 1 | `jobs_dedup` prefix |
| Start-up migration | 3, in one transaction | |
| Take a budget's token | 1, plus 1 read after a wait to check for a pause | `rate_buckets` primary key |
| Pause a budget after a `429` | 1 | `rate_buckets` primary key |
| Report what an answer said is left | 1, only when the recipe names rate headers; writes only when the reading lowers the bucket | `rate_buckets` primary key |

Known limits:

- `GetJobsAsync` filtered by owner or status walks the primary key backwards and filters. That is fine for admin screens. If it becomes hot, add an index on `(queue, owner, id)` in a migration.
- Workers poll every `PollInterval`. `LISTEN`/`NOTIFY` would cut the wait for new jobs; it is on the ideas list.
- Telemetry costs nothing without a listener: instruments and spans are no-ops until something subscribes.

## Security model

Trust boundaries and what crosses them:

| Boundary | What crosses | Rule |
| --- | --- | --- |
| Configuration to SyncLane | Connection string, schema name | The connection string is never logged. The schema name must match `^[a-z_][a-z0-9_]{0,62}$` before it reaches SQL. |
| SyncLane to PostgreSQL | Job state | Values are always parameters. Only the validated schema name is interpolated. |
| SyncLane to user handlers | The `Job` row | Handlers look up credentials themselves; SyncLane never sees them. |
| Credential store to `SyncLane.Http` | One owner's credential, for one page | Added to the request only. Never stored, logged, traced or put in a message. OAuth 2.0 access tokens are kept in memory, never written. |
| `SyncLane.Http` to an API | Requests carrying credentials | Only to the recipe's host and its token URL, both fixed text. No redirects; next-page URLs on another host are refused; a signer cannot move a request. |
| API answers to the table | Error messages and cursors | Messages never quote a body, a URL or a network exception's text. A query-string key is taken out of next-page URLs before they become cursors. |
| Handler exceptions to the table | Exception type and message | Capped at 2,000 characters. Handlers must not put secrets in exception messages ([SECURITY.md](SECURITY.md)). |
| SyncLane to observability | Metrics, spans, logs | Queue, type and budget name only. Never owner, lane, budget key, cursor, headers or bodies. |
| SyncLane to the internet | Nothing of its own | No telemetry home, no license check, no update check. `SyncLane.Http` calls only the hosts your recipes name. |

Anyone who can write to the `jobs` table can make workers run a handler for any owner. Treat write access to it like access to the application.

## Extension points

Each is a small interface the user implements; SyncLane calls it.

| Interface | Status | Purpose |
| --- | --- | --- |
| `IJobHandler` | Available | Run one page of a job and return the next cursor. |
| `IOwnerSource` | Available | List the accounts a queue should plan for. |
| `IRecordSink` | Available in `SyncLane.Http` | Write the records of a page. Ready-made sinks (PostgreSQL `jsonb`, HTTP POST) are phase 3. |
| `ICredentialStore` | Available in `SyncLane.Http` | Give an owner's credential for a page, and save a rotated refresh token. Credentials stay in the user's storage, outside the jobs table. |
| `IRequestSigner` | Available in `SyncLane.Http` | Sign an outgoing request for APIs that require it. May add headers and query parameters, never move the request. |
| Rate budgets | Available | `RateBudgets` for hand-written handlers, `rate` in recipes. See [docs/design/rate-budgets.md](docs/design/rate-budgets.md). |

## Packages

| Package | Contents | Status |
| --- | --- | --- |
| `SyncLane` | Engine: planning, running, storage, results, errors, telemetry | This repository |
| `SyncLane.Http` | Declarative HTTP source: recipes, pagination, auth, rate budgets | Preview: recipes run, page and authenticate ([docs/recipes.md](docs/recipes.md)); checked against five public APIs ([samples/LiveCheck](samples/LiveCheck)) |
| `SyncLane.Sinks.*` | Destinations | Planned |
| `SyncLane.Cli` | `init --curl`, `probe`, `test` | Planned |
| Adapters to in-house packages | For example `SyncResult` to `ApiResult` | Separate solution, never in this one |

## Decision log

| # | Decision | Why | Status |
| --- | --- | --- | --- |
| D1 | PostgreSQL only | The guarantees rest on `FOR UPDATE SKIP LOCKED` and partial unique indexes. | Accepted |
| D2 | One table for all job state | State the user can read with `SELECT` and fix with `UPDATE`. | Accepted |
| D3 | At-least-once pages | Exactly-once across an external API is not possible without the API's help; upserts make repeats harmless. | Accepted |
| D4 | Apache License 2.0 | Permissive, with an explicit patent grant; warranty disclaimer and liability limit as in MIT. | Accepted |
| D5 | Results for expected failures, exceptions for everything else | A failed result must never hide an outage. | Accepted |
| D6 | Own result types, no in-house packages | The library is public and must build alone; names must not collide with applications' own types. | Accepted |
| D7 | Cursor pagination, total on request | Constant cost per page; no `COUNT(*)` by default. | Accepted |
| D8 | Telemetry through `System.Diagnostics` | No dependency; works with OpenTelemetry, Prometheus exporters and `dotnet-counters`. | Accepted |
| D9 | Append-only, idempotent migrations | Safe rolling deploys and safe hand-applied scripts. | Accepted |
| D10 | One message catalog with stable codes | Consistent wording, searchable docs, localization by code. | Accepted |
| D11 | Interfaces only at extension points | Testing against real PostgreSQL makes internal interfaces cost without benefit. | Accepted |
| D12 | Rate budgets separate from lanes | Lanes serialise; shared limits should not. | Accepted |
| D13 | `SyncLane.Http` depends on `Microsoft.Extensions.Http` | `IHttpClientFactory` manages connection lifetimes and lets users add handlers to a named client; writing that ourselves would be worse. Maintained by Microsoft, MIT licensed. The engine does not take the dependency. | Accepted |
| D14 | The HTTP source follows no redirects | A redirect could carry a recipe's headers to another host, and would break the rule that a recipe only calls the host it names. | Accepted |
| D15 | Credentials never in a recipe or the jobs table; an `ICredentialStore` gives them per owner, once per page | A recipe is meant to be shared and committed, and the jobs table is read by dashboards and support staff. Asking per page makes a rotated or revoked credential count at once; a store that is slow to ask can cache. | Accepted |
| D16 | OAuth 2.0 access tokens live in memory only | Writing them down would add a table of live secrets to protect. A restart costs one token request per owner. | Accepted |
| D17 | A network error keeps its kind, not its message | The engine stores every message of an exception chain, and the messages of `HttpClient` and proxies can hold the URL, with a key in its query, or a proxy's password. The kind (DNS, connection, TLS, proxy) is enough to act on. | Accepted |
| D18 | Rate budgets use a token bucket of our own, not `System.Threading.RateLimiting` | The library's bucket cannot be paused or drained from outside and does not run on `TimeProvider`, and sharing it across instances needs its state in a row anyway. Since D21 the bucket is three SQL statements, tested with explicit times. | Accepted |
| D19 | A `429` pauses the whole budget, not only the job that got it | Limits belong to accounts. Other jobs on the account would only collect their own `429`s from the same empty bucket; ShipStation's documentation asks for exactly this. | Accepted |
| D20 | A job that only has to wait gives its slot back without using an attempt (`JobPostponedException`) | Waiting for a token is not a failure. Counting it would abandon jobs during a long pause; holding the slot would starve other work. | Accepted |
| D21 | Rate buckets live in PostgreSQL, one statement per token, no batches and no in-memory mode | A budget kept per process lets two instances send twice the limit, and asks users to divide limits by hand. One locked row per bucket is exact. Measured on a local PostgreSQL 18: about 0.16 ms per token, and about 12,000 tokens a second on one key with eight callers, against APIs that allow a few to a few dozen requests a second and answer in 50 ms or more. Reserving tokens in batches would save little and hold tokens other instances could use. | Accepted |
| D22 | A `429` for a request sent before the latest pause does not start another pause | Requests in flight together come back together. Counting each one doubled the backoff once per request: eight `429`s without `Retry-After` paused an account for over two minutes instead of one second. A permit carries the bucket's pause count, so a late `429` is recognised. It may still lengthen the pause if its `Retry-After` asks for longer. | Accepted |
| D23 | Rate headers are read only when a recipe names them, and a reading only lowers a budget | No two of the APIs studied write them alike (docs/design/rate-budgets.md), the IETF field has changed shape twice in its drafts, and an API may count a partition other than the budget's key. A declared budget stays the ceiling; a header can only make SyncLane more careful. | Accepted |
| D24 | A reading subtracts the requests that took a token after the one answered | The API counts a request when it arrives; requests already sent are not in the count. Without this, four requests in flight against a window with two left all go. A `taken` counter on the bucket's row (migration 3) gives each token a sequence number. The error is on the safe side: a request that overtakes another is subtracted twice. | Accepted |
| D25 | A `403` with `Retry-After`, or whose declared remaining header is zero, is a throttle | GitHub answers a spent limit with `403` and `x-ratelimit-remaining: 0`; treating it as refused credentials abandoned jobs that only had to wait. A `403` without either signal still abandons. | Accepted |
