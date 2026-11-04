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
| `RateBudgets` | One token bucket per budget and key, shared by every instance: requests wait for tokens, a `429` pauses the bucket and lowers its rate, and the rate climbs back below the declared limit. | The learning rule changes, after a new measurement. |
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

Six tables in the configured schema.

`jobs` holds one row per job: what it covers (`queue`, `owner`, `lane`, `type`, `mode`, `window_start`, `window_end`, `dedup_key`), where it is (`status`, `cursor`, `processed_count`, `attempt_count`, `error`, `next_run_at`) and who holds it (`leased_by`, `lease_expires_at`, `heartbeat_at`). The full definition is in [docs/schema.sql](docs/schema.sql).

`coverage` holds one row per queue, owner and type: what has been planned (an initial job exists, the oldest initial start, the newest window end, the last planned and last recurring times) and how far completed data reaches (`completed_to`, `completed_at`). The planner reads it instead of every job ever created. It is written by the same statement that inserts jobs (D40), so old jobs can be deleted without changing what is planned.

`job_stats` counts, per queue, job type and hour: completed jobs, their records, failed attempts and abandoned jobs. The statements that complete and settle a job update it (D45). It is kept for 400 days, long after clean-up deletes the jobs, and the monitor's overview and volume read it instead of jobs.

`instances` holds one row per running process: its application, host, version, the queues it runs and its budgets, with a heartbeat every 30 seconds. Its id is the prefix of the worker names in `jobs.leased_by`. It keeps two applications off one queue (D43) and shows what is running.

`migrations` records which schema steps the database has.

`rate_buckets` holds one row per rate budget and key: the tokens left, when they were last refilled (in the future while the bucket is paused), how many pauses it has had (`generation`), how many tokens it has given (`taken`), and its learned rate (`rate`, with `rate_max`, the rate that last caused a throttle). Its primary key `(budget, key)` serves every statement on it.

The guarantees are indexes, not code:

| Index | Guarantee |
| --- | --- |
| `UNIQUE (queue, owner, type, dedup_key)` | A planned job is created once, however many instances plan at once. |
| `UNIQUE (queue, lane) WHERE status = 'running'` | One running job per lane, across every process. |
| `(queue, next_run_at) WHERE status IN (...)` | Finding the next due job stays cheap as completed rows pile up. |
| `jobs_open (queue, owner, type) WHERE status <> 'completed'` | The planner sees what is still under way per owner and type without reading history. |
| `jobs_finished (queue, completed_at) WHERE status = 'completed' AND mode <> 'initial'` | Clean-up finds old jobs without scanning the table. |
| `jobs_initial (queue, owner, type) INCLUDE (status) WHERE mode = 'initial'` | Progress and the monitor count an owner's history without reading its recurring jobs. |

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
| Plan a queue | 2: summary read, one insert that also replaces abandoned recurring jobs and updates the summary | `coverage` primary key, `jobs_open`, `jobs_dedup` |
| `MarkLoadedAsync` | 1 | `jobs_dedup`, `coverage` primary key |
| `ForgetOwnerAsync` | 1 | `jobs_dedup` prefix, `coverage` primary key |
| Clean-up | 1 per batch of 1,000, hourly | `jobs_finished` |
| Register an instance | 2 at start-up, in one transaction behind an advisory lock | `instances`, a handful of rows |
| Heartbeat | 1 every 30 seconds, also deleting rows silent for a day | `instances` primary key |
| Monitor repairs | 1 each | Primary key; `jobs_open` for a code or an owner |
| Monitor reads | 1 each. Measured on a million jobs: overview 0.2 ms, freshness page 0.9 ms, problems 0.3 ms, a week by hour 0.8 ms, 400 days by day 46 ms | `coverage`, `job_stats`, `jobs_open`, `jobs_initial`, `rate_buckets`, `instances` |
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
| `IRecordSink` | Available in `SyncLane.Http` | Write the records of a page. `WriteToPostgres` and `WriteToHttp` are built in; `RecordIdentity` gives custom sinks the same keys, versions and duplicate removal, and `SinkConformance` checks them against replays. |
| `ICredentialStore` | Available in `SyncLane.Http` | Give an owner's credential for a page, and save a rotated refresh token. Credentials stay in the user's storage, outside the jobs table. |
| `IRequestSigner` | Available in `SyncLane.Http` | Sign an outgoing request for APIs that require it. May add headers and query parameters, never move the request. |
| Rate budgets | Available | `RateBudgets` for hand-written handlers, `rate` in recipes. |

## Packages

| Package | Contents | Status |
| --- | --- | --- |
| `SyncLane` | Engine: planning, running, storage, results, errors, telemetry | This repository |
| `SyncLane.Http` | Declarative HTTP source: recipes, pagination, auth, rate budgets, the PostgreSQL and HTTP destinations | Preview: recipes run, page and authenticate ([docs/recipes.md](docs/recipes.md)); checked against five public APIs ([samples/LiveCheck](samples/LiveCheck)) |
| `SyncLane.Sinks.*` | Destinations for other databases, one package each, on the public `RecordIdentity` | Planned |
| `SyncLane.Cli` | The `synclane` command: `init` turns a curl into a recipe, `test` runs one for two pages | Preview ([docs/recipes.md](docs/recipes.md#starting-from-a-curl)) |
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
| D23 | Rate headers are read only when a recipe names them, and a reading only lowers a budget | No two of the APIs studied write them alike, the IETF field has changed shape twice in its drafts, and an API may count a partition other than the budget's key. A declared budget stays the ceiling; a header can only make SyncLane more careful. | Accepted |
| D24 | A reading subtracts the requests that took a token after the one answered | The API counts a request when it arrives; requests already sent are not in the count. Without this, four requests in flight against a window with two left all go. A `taken` counter on the bucket's row (migration 3) gives each token a sequence number. The error is on the safe side: a request that overtakes another is subtracted twice. | Accepted |
| D25 | A `403` with `Retry-After`, or whose declared remaining header is zero, is a throttle | GitHub answers a spent limit with `403` and `x-ratelimit-remaining: 0`; treating it as refused credentials abandoned jobs that only had to wait. A `403` without either signal still abandons. | Accepted |
| D26 | Every budget learns its rate: per-token AIMD under the declared limit | Declared limits are often wrong or shared. Simulated against hidden limits, cutting to 0.7 on a fresh `429` and raising the rate per token taken while the budget is the bottleneck (2% below 90% of the last refused rate, 0.03% up to it, 0.1% above) kept `429`s under 1% at 91 to 100% throughput. Time-based growth failed at low rates, and a scale-free CUBIC collapsed on large buckets. A budget whose limit is right never changes. | Accepted |
| D27 | A pause doubles only while the first request after the previous pause is refused again | Doubling on every `429` until a quiet spell punished a rate that was being lowered step by step, and drove pauses to five minutes. Measured, the old rule cut a window without headers from 91–96% to 72% throughput. | Accepted |
| D28 | Attempts count failures since the last saved page | A saved page proves the attempt works and is never lost, so a job that keeps making progress always reaches the end. Counting over a job's life abandoned long jobs for failures hours apart. | Accepted |
| D29 | With a rate budget, a throttle postpones the job instead of using an attempt | The budget's pause already protects the API, growing to five minutes while the first request after each pause is refused. Counting attempts on top abandoned jobs during an hour of throttling, though nothing was wrong with them. The cost: a job on an account that refuses forever waits forever; `synclane.budget.paused` and the `SL5105` in its row show it. | Accepted |
| D30 | A destination needs a key, and infers one only when it is safe | A page is delivered at least once, so without a key a repeat is a duplicate. `$.id` is used when every record of the page has a unique scalar one; anything else stops the job and names the fields that could serve. Records that never change can be keyed by a hash of their canonical content. Appending without a key is never a fallback. | Accepted |
| D31 | The Postgres destination writes a page with one `INSERT … SELECT FROM unnest(…) ON CONFLICT`, not `COPY` into a temporary table | Measured on PostgreSQL 18 with pages half new and half changed: 0.71 ms against 1.50 for 100 records, 12.98 against 14.65 for 2,000, 73 against 76 for 10,000. `COPY` needs a transaction, a temporary table and a second statement, four more round trips over a network. | Accepted |
| D32 | A destination owns its table, and a row is rewritten only when its record changed | No DDL for the user, and a fixed shape SyncLane can rely on. Skipping unchanged rows means syncing the same data again writes nothing, which saves write-ahead log and bloat. Versions are compared as numbers or as instants, never as text. | Accepted |
| D33 | The HTTP destination's body holds no time, and its id is a hash of the body | The same records then always make the same bytes and the same id. A page sent again carries its old id, so a receiver can drop it, and a page whose records changed carries a new one, so no id is ever reused with a different payload. That is what both Standard Webhooks and the `Idempotency-Key` draft require. The send time travels in the signed `webhook-timestamp` header. | Accepted |
| D34 | The HTTP destination retries in the page, and postpones when the receiver asks to slow down | Failing the attempt would fetch the source page again and spend the source's rate budget to resend records already in hand. Three quick tries cover a blip; a receiver that asks to slow down is pacing, not failing (as D29), and a wait over 30 seconds does not hold the worker. | Accepted |
| D35 | Custom sinks get a conformance check, not examples | Examples show the happy path; sinks break on replays. `SinkConformance.CheckAsync` replays pages the way workers do and reads the store back, returns a result, and depends on no test framework. A sink with its own record shape maps the samples with one function. | Accepted |
| D36 | The recipe's JSON Schema is generated from the recipe types and their XML comments, not written by hand | A hand-written schema drifts from the parser. Generated, it has the same fields, required ones and choices as `Parse`, the descriptions users already read in the API docs, and a description for each choice. It is checked in like `docs/schema.sql`, embedded in the package, and checked against the live check's recipes by a small validator in the tests instead of a validator package. | Accepted |
| D37 | The `synclane` command is built on System.CommandLine | It gives help, version, option errors and POSIX conventions that a hand-written parser would have to grow one by one, and it is what the `dotnet` command itself uses. MIT, maintained by Microsoft's .NET team; version 2.0, stable. Only the tool package takes it: the engine and `SyncLane.Http` stay as they were. The tool also references `Microsoft.Extensions.DependencyInjection`, already in the graph through `Microsoft.Extensions.Http`, to run a recipe through the public `AddSyncLaneHttp`. | Accepted |
| D38 | `synclane init` takes the curl after the command, not inside an option, and learns from one real answer, not from an API description | A copied curl holds both kinds of quotes, so wrapping it in one more pair is where people fail; after the command, the shell splits it as it would for curl, and a piped curl goes through the same splitter. An answer shows what the API does; a description shows what it says it does. Every inferred field gets a comment in the written file, so the person reviews the file, not a log. | Accepted |
| D39 | `synclane test` prints the shape of the data, never the data | Field names, counts, whether the key is unique and whether page 2 brought new records are what decides whether a recipe works; record values, cursors, URLs and credentials are what leaks when output is pasted into an issue or a CI log. A page 2 that repeats page 1, the commonest wrong guess, fails the test with its own code. | Accepted |
| D40 | The planner reads a summary that the inserting statement keeps, not the history | Every fact planning needs is fixed when a job is created, so a data-modifying CTE upserts it in the same statement: one round trip, never out of step. Reading every job made planning grow with time (200 owners: 1.7 ms at 10 periods, 171 ms at 5,000; now 0.2 ms at both) and kept old rows from being deleted. Proposals that hit an existing key are counted too, so jobs an older instance created during a rolling deploy reach the summary. What is still under way is read from open rows, which clean-up never deletes. | Accepted |
| D41 | An abandoned recurring job is replaced by the next period's job, whose window reaches back over it | Keeping them grows the table by one row per period for every broken account; deleting them loses the windows they missed without a trace; stopping until a person requeues halts a whole queue after a short outage. Replacing keeps one row, refetches what was missed once the cause is gone, and leaves the current error on the newest job. The delete is conditional on the row still being abandoned, so a concurrent requeue wins. History slices still wait for a requeue: they are bounded and each one matters. | Accepted |
| D42 | Completed recurring and manual jobs are deleted after `KeepCompletedJobs`, 7 days by default; initial jobs are kept | Recurring jobs are what grows. Initial jobs are bounded by the horizon, `GetProgressAsync` counts them, and they record what history was loaded. A week answers "what happened last Tuesday" and costs little: the table holds about a week of recurring jobs, whatever its age. Each application cleans only its own queues, so applications sharing a table keep their own rules. | Accepted |
| D43 | A queue belongs to one application at a time, checked at start-up from a table of live instances | A worker takes any job of a queue it declares. Two applications sharing a schema with one queue name took each other's jobs and abandoned the types they did not know. Each process registers its application and the queues it runs, under an advisory lock, and does not start when a live instance of another application runs one of them. Copies of one application share queues. An instance that only calls `ISyncLane` claims none. A heartbeat every 30 seconds keeps a claim; two minutes of silence ends it, so a crashed application does not block its successor for long. A budget declared differently elsewhere only warns, because a deploy that changes a limit passes through that state. | Accepted |
| D44 | The monitor answers questions, reads every queue, and never reads history | A list per job state leaves the reader to work out what is wrong. Six calls answer what operators ask: the state of each queue, whose data is old and why, what fails grouped by code, budgets, instances, volume. They need no queue declared, so one application can watch all that share a schema. Each reads the summaries (`coverage`, `job_stats`), the open jobs and small tables, so its cost stays flat as jobs pile up; lists page by cursor. Measured on a million jobs: every call under a millisecond except a year of daily volume, 46 ms. | Accepted |
| D45 | Completed and failed work is counted per hour in `job_stats`, by the statement that ends the attempt | Counting from jobs would scan a week of rows per poll, and clean-up would cut charts off at seven days. One upsert per finished attempt into a row per queue, type and hour costs little: completions are per job, not per page, and a row sees about one write a second at a thousand owners on 15-minute periods. Records are counted when their job completes. | Accepted |
| D46 | Repairs live on the monitor, need no declared queue, name one target, and never touch a running job | The application that watches every queue has no handlers, so repairs cannot depend on declarations. Each names a job, an error code or an owner, so no call can requeue everything by accident. Each update is conditional on the row's own status at the moment of the write, so a job a worker takes in the meantime is left alone. A call about one job says why it did nothing; a call about many returns its count. Prioritizing lowers the owner's priorities by a fixed amount once, so asking twice changes nothing and its own type order stays. | Accepted |
