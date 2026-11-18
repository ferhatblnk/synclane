# Changelog

All notable changes to this project are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Two applications that took the same name are told of each other. Each instance records what it declares, every job type with its queue and handler (migration 16 adds `instances.declares` and an index on `instances.application`), and with each heartbeat compares it with the live instances of its name. One that declares nothing in common is a warning in the log, a Degraded health check and `InstanceInfo.SharesNameWith` in `GetInstancesAsync` and `GET /instances`. Copies, web instances and a deploy that adds or removes a queue are not reported. The default name is the entry assembly's, which projects built from one template often share. See [Several applications on one schema](docs/operations.md#several-applications-on-one-schema) and D69.

- The monitor shows the instances a switch cannot reach: `SwitchInfo.IgnoringSwitch` counts the live instances of an application and role that run a version before 0.7, which do not read switches and run as their own settings say, and `InstanceInfo.FollowsSwitches` marks each instance. A switch that seems to have no effect on part of an application is explained before anyone looks at a server.

### Changed

- The documentation no longer turns workers and the planner off in settings. What a process is comes from its settings (`SyncLane:Application`, and `SyncLane__Role` where one build runs as web and worker); whether it works is a switch in the database, set from the monitor, which survives restarts and new replicas because every instance reads it when it starts. [Integrating](docs/integrating.md) now sets a name of its own and queue names that start with it, sets the web and worker switches before the first deploy, and asks for authorization on the monitor before anything else. `RunWorkers` and `RunPlanner` keep their default and behaviour. See [Identity and control](ARCHITECTURE.md#identity-and-control) and D70.

## [0.7.0] - 2026-11-18

An application's workers and planner can be switched on and off from the monitor, per application and role, without a deploy. Migration 15 adds the `switches` table and four columns to `instances`; with no switch set, everything runs as before. Instance ids change form. A page takes an existing application from nothing to production, written to be handed to an AI assistant.

### Added

- `options.Role` (`SyncLane:Role`): what this copy of the application does, for one build deployed as web instances and a worker. Each instance records its role and whether it runs workers, the planner and listening, and `GetInstancesAsync` and `GET /instances` show them (`InstanceInfo.Role`, `RunsWorkers`, `RunsPlanner`, `ListensForChanges`). Migration 15 adds those columns and the `switches` table (#56).
- Workers and the planner can be switched on and off without a deploy: a row in `switches` for an application and role overrides `RunWorkers` and `RunPlanner` where it holds a value. Instances read it at start-up and with every heartbeat; one whose workers run hears a change at once. Switched-off workers hand their running jobs back with their cursors, without using an attempt. A switch that would run a queue another application runs leaves the instance off, and its health check says why. Set them from the monitor: `SetSwitchAsync` and `GetSwitchesAsync`, `POST /switch` and `GET /switches` over HTTP; every change is in the actions log with who and why. `SL3015` refuses a name no instance could have. `QueueOverview.Planners` and `Workers` count the live instances that plan a queue and run its workers. See [Switching an application on and off](docs/operations.md#switching-an-application-on-and-off) and D68 (#58, #59).
- [Integrating SyncLane into an application](docs/integrating.md): one page from nothing to production, written so it can be handed to an AI assistant in your project. It starts from what the project does today (one row per existing job, with its rules, keys and side effects), then the questions to settle with their defaults, handler or recipe, which process does what and where the code lives, each step with a check, and the gates that prove the integration: the existing tests unchanged, an end-to-end test against PostgreSQL, and the old and new data compared until they match. `llms.txt` lists it first (#55).

### Changed

- An instance's id is now its host, its process id and a random part. Two containers that share a hostname and both run as process 1 had the same id, so the monitor showed them as one instance; job writes were already safe through the lease generation. Worker names in `leased_by` start with the new id (#56).

## [0.6.3] - 2026-11-18

Taking a job stays fast when many jobs wait: a new index and fresh statistics after a large planning round, found by a load test that ships with the repository. Migration 14 adds one index; nothing else changes for your code.

### Added

- A load test you can run yourself: [samples/LoadTest](samples/LoadTest/README.md) starts PostgreSQL, a pretend API with a hidden limit and SyncLane instances, loads the history of 1,000 accounts, and reports how fast it drained and whether any page was lost, written twice, or sent above an account's budget, with the pretend API as an independent witness (#52).

### Changed

- Taking a job no longer slows down as jobs pile up. The lease read and sorted every waiting job to find the next one; with 25,000 jobs waiting and 64 slots asking at once that took 219 ms on average and up to 15.6 s, and the queue drained at half its speed. It now walks a new index in its own order (`jobs_lease_order`, migration 14) and stops at the first job it may take, and a planning round that adds at least 1,000 jobs, more than a tenth of the table, refreshes the table's statistics at once instead of waiting up to a minute for autovacuum. In the load test with a fast API the same history drained in 47 s instead of 143 s, 5,331 pages a second instead of 1,749; with a realistic API it reached 97% of what the rate budgets allow, up from 88%. Migration 14 builds an index on the jobs that have not completed; on a large table see [Upgrading the schema](docs/operations.md#upgrading-the-schema) for building it without blocking writes (#53).
- Two pictures: the architecture opens the README (the schema and the indexes behind the guarantees, the engine's parts inside your application, the recipe pipeline, and what it promises), and how a job runs opens [how-it-works.md](docs/how-it-works.md) (its path, its failures, a worker going away, a `429`). Three diagrams join the docs: a page from lease to saved cursor ([how-it-works.md](docs/how-it-works.md#running-a-job)), a `429` pausing a budget on every instance, and a curl becoming a recipe ([recipes.md](docs/recipes.md)) (#51).
- The README says what SyncLane aims to be: quick to connect, compact, taking the hard parts on itself, safe by default, and open; and that support for other databases than PostgreSQL is planned (#51).

## [0.6.2] - 2026-11-17

The monitor's HTTP endpoints take a status or a unit in the query string the way they write it. No migration.

### Fixed

- `GET /jobs?status=` and `GET /volume?unit=` refused `pending`, the very word the endpoints answer with, and answered only `Pending`: the framework binds an enum from the query string case-sensitively. Both now take the name in any case (#49).

## [0.6.1] - 2026-11-17

A worker whose lease was taken over can no longer write to the job, even when the worker that took it over has the same name. Migration 13 adds one column; apply it before or with the upgrade.

### Fixed

- A worker whose job was taken over could still write to it when the worker that took it over had the same name, as two containers that share a hostname and both run as process 1 do. Every job now counts its leases (`lease_generation`, migration 13), and a page saved, a heartbeat, a completion, a failure, a postponement or a hand-back is written only while the worker still holds that very lease. A worker that pauses past its lease writes nothing after the takeover, whatever it is called.

## [0.6.0] - 2026-11-06

Deliveries in order: ordered queues hold a lane behind a job that failed until it is fixed or skipped, and a job can say who asked for it. The README is now a way in, with the reference in `docs/`. Migration 12 adds one column and one index; apply it before or with the upgrade.

### Added

- Ordered queues: `queue.Ordered` (also from configuration) takes each lane's jobs strictly in the order they were created. A job that failed or was abandoned holds the jobs behind it in its lane until it completes or is skipped; other lanes and other queues go on. The lease finds the head of each lane through a new index (migration 12): about 6 ms on 100,000 waiting jobs in 1,000 lanes.
- `ISyncLaneMonitor.SkipJobAsync(id, reason)` and `POST /jobs/{id}/skip`: take one job that is not running out of line for good, logged with who and why.
- `NewJob.RequestedBy` and `Job.RequestedBy`: who or what asked for a job, for the handler and the monitor. A sync-now records its caller. Code `SL3014` for one that cannot be used.

### Changed

- The README is a way in: what SyncLane is, a quick start with a recipe or a handler, and "What do I use for..." tables for getting data sooner, stopping and repairing, and sending data out. The reference moved to [docs/jobs.md](docs/jobs.md), [docs/monitoring.md](docs/monitoring.md), [docs/configuration.md](docs/configuration.md) and [docs/demo.md](docs/demo.md); a job's row, its life and the two indexes to [docs/how-it-works.md](docs/how-it-works.md).

## [0.5.0] - 2026-11-06

Priorities, a health check and sync-now for operators; recipes for APIs that need values and hosts per account; and recipes written with an AI assistant. Migration 11 adds one column; apply it before or with the upgrade.

### Added

- Priority levels: `JobPriority` (`Urgent`, `High`, `Normal`, `Low`).
  - `NewJob.Priority` gives one job its level; `JobOwner.Priority`, from the owner source or the enqueue, gives an owner's jobs theirs, for example `High` for a new customer whose history should load first.
  - Due jobs run urgent first, then owners an operator moved forward, then high, normal and low; within a level, types keep their declared order. A running job is never cut.
  - `Job.PriorityLevel` and `Job.Prioritized` say where a job stands; the monitor's job view carries both.
  - No migration: rows written before read as normal, and owners moved forward before stay moved.

- `SyncLaneHealthCheck` for .NET health checks: `services.AddHealthChecks().AddCheck<SyncLaneHealthCheck>("synclane")`.
  - Unhealthy when the database cannot be reached, the schema lacks migrations, or this instance has not renewed its registration for two minutes.
  - Degraded while starting, when a queue's latest planning round failed, or when the instance does not hear job changes.
  - New dependency of the engine: `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` (MIT, Microsoft).

- `ISyncLaneMonitor.SyncNowAsync(queue, owner, type)` and `POST /sync-now`: fetch an owner's new data now, for example when a webhook says something changed.
  - The job covers from where the owner's completed data reaches to now, or the whole of a type without windows, at `High` priority, in the owner's lane.
  - While it waits, another call stretches its window to now instead of adding a job, so a burst of webhooks is one fetch; calls at the same moment make one job too.
  - Like the other repairs it needs no queue declared, and it is written to the actions log with who asked. Code `SL3012` when the queue never planned that owner and type.

- `synclane mcp`: the recipe tools for an AI assistant, over the Model Context Protocol (stdio).
  - Tools: `synclane_init`, `synclane_test`, `synclane_check`, `synclane_recipe_guide`, `synclane_recipe_schema`, `synclane_explain_error`. They run the same code as the commands and return what the commands print: structure, never records, cursors or credentials.
  - `$NAME` in a curl given to `synclane_init` is read from the server's environment, so a token never has to enter the conversation.
  - The tools read and write only inside the folder the server starts in, or `--folder` (`SL6012`, `SL6013`).
  - It runs on the person's machine with their own assistant; SyncLane calls no AI service and opens no port.
  - New dependency of the tool: `ModelContextProtocol.Core` (Apache-2.0), and with it `Microsoft.Extensions.AI.Abstractions` (MIT). Both are listed in THIRD-PARTY-NOTICES with their licenses.
- `llms.txt` at the root of the repository, and [docs/ai.md](docs/ai.md) on writing recipes with an assistant.
- Values per owner: `JobOwner.Values`, given by the owner source or an enqueue, and `{{owner.<value>}}` in recipes, for what an API needs besides the key (a marketplace, a region, a store id).
  - Kept with the owner like its name (migration 11, `owners.owner_values`) and returned with every job as `Job.OwnerValues`: no lookup per page. An owner given without values keeps its old ones.
  - Not secrets: they may land in URLs and cursors. Never written to a log, a span or a metric.
  - Codes: `SL3013` for a name or value that cannot be used, `SL5132` when a job's owner lacks a value its recipe uses. `synclane test --value name=value`, and `values` in the MCP test tool (`SL6014`).
- A recipe's `rate.key` can use the owner's values, such as `{{owner.seller}}`, so owners that share an account's limit (several stores of one seller) share its bucket.
- A host per owner: `request.hosts` lists every host a URL like `https://{{owner.endpoint}}/v1/orders` may make, or `*.example.com` for one name under a domain. Every request's host is checked before it is sent (`SL5133`), and a next page must stay on the job's own host.
- An OAuth 2.0 access token in a header of the recipe's choosing: `name` (and `prefix`) on `oauth2` auth, for APIs that do not take it in `Authorization: Bearer`. Renewal, retries on 401 and rotated refresh tokens work as before.
- `{{request.id}}`: a new UUID for every request, for APIs that want a correlation id per call.
- `ListenForChanges`: turn listening off where no connection can get around a pooler in transaction mode. No connection is opened and no probe is sent; workers poll every `PollInterval`.

### Changed

- A completed manual job whose window starts within an owner's completed data extends it, never past now. One past a gap does not, so a gap is never hidden.
- `PrioritizeOwnerAsync` now moves high and low jobs too, and leaves urgent ones where they are.

## [0.4.1] - 2026-11-06

Live changes behind a connection pooler.

### Added

- `ListenConnectionString`: the connection the change feed listens on, when `ConnectionString` goes through a pooler in transaction mode. Settable from configuration as `SyncLane:ListenConnectionString`. Left out, the feed listens on `ConnectionString` as before.

### Fixed

- Behind a pooler in transaction mode, listening failed without a word, and harmed the pooler's other clients.
  - Workers polled and live views stayed quiet, and nothing said why.
  - The pooler kept the `LISTEN` on a server connection, and every job change then reached whichever client it lent that connection to next. Measured through PgBouncer 1.26 with one such connection and one change a second, other clients timed out a few times a minute; at ten changes a second, several times more.
  - Now each new listening connection first listens on a channel of its own, sends a probe there through `ConnectionString` and waits five seconds to hear it. Only then does it listen on the schema's channel. If it hears nothing, it logs a warning that names `ListenConnectionString`, closes and stops listening until the next start, and workers poll every `PollInterval`.
  - Upgrading from an earlier version behind PgBouncer: run `RECONNECT` on its admin console, or restart it, to close the server connections that still listen. See [docs/operations.md](docs/operations.md#behind-a-connection-pooler).
- The README said transaction mode is PgBouncer's default. It is session mode.

### Changed

- Dependencies: `Npgsql` 10.0.3 and `Microsoft.Extensions.*` 10.0.14. An application that uses SyncLane needs at least these versions.
- `THIRD-PARTY-NOTICES.txt` names the libraries the tool carries without their versions, so an update cannot leave it stale.
- CI also runs the whole test suite through PgBouncer in transaction mode. `SYNCLANE_TEST_LISTEN_DB` gives the tests that listen a connection around the pooler.

## [0.4.0] - 2026-11-05

Ready for real use: owners people can find by name, instances that stay visible after they stop, and numbers per environment from configuration. The packages are no longer previews: install them without `--prerelease`. Before 1.0 a minor version may still change the API, as this one does.

### Added

- Owner names.
  - `JobOwner` takes an optional `Name`, for example a customer and its store. The owner source gives it, and so can `NewJob.Owner`.
  - A new name replaces the old one at the next planning round, and an owner given without a name keeps its old one.
  - `Job.OwnerName` and `OwnerFreshness.OwnerName` carry it.
  - The name is the owner's data. It is stored and shown, and never written to a log, a span, a metric tag or a message.
- Search and a type filter.
  - `JobQuery.Search` and `FreshnessQuery.Search` find owners whose key or name contains the text, ignoring case. `%` and `_` match only themselves. Code `SL3011` for a search that is empty or longer than 100 characters.
  - `JobQuery.Type` lists the jobs of one type.
  - A search reads the new `owners` table first, then each matching owner's newest jobs. On a million jobs it takes under a millisecond for one owner and 18 ms when every owner matches.
- Settings from configuration: `AddSyncLane(configuration, options => ...)`.
  - Pass the section, for example `builder.Configuration.GetSection("SyncLane")`.
  - What it can set:
    - top-level settings;
    - `Queues:<queue>` for concurrency, attempts, lease and delays;
    - `Queues:<queue>:Types:<type>` for horizon, slice, every, overlap and offset;
    - `Budgets:<budget>` for limit, per, burst and start.
  - It overrides what the code declares and never adds to it.
  - Every problem is reported at once:
    - a name the code does not declare stops the start with `SL1012`, naming its path;
    - a value that cannot be read stops it with `SL1013`, naming the path and never the value.
  - Names ignore case, so environment variables in capitals work.
  - No package is added.
- `InstanceInfo.StoppedAt`. `GET /instances` shows it too.
- `SyncLane.AspNetCore`: `GET /jobs?type=&search=`, `GET /freshness?search=`, and `ownerName` on every job.
- `THIRD-PARTY-NOTICES.txt`, in the repository and in the `SyncLane.Cli` package: the notices of the libraries the tool carries (Npgsql, `Microsoft.Extensions.*`, `System.CommandLine`).
- `AUTHORS`, which says who "The SyncLane Authors" are.

### Changed

- **Breaking:** `JobOwner` has a third positional parameter, `Name`. Source that builds it with one or two arguments compiles as before; code compiled against an older version must be rebuilt.
- An instance that stops gracefully now marks its row stopped instead of deleting it.
  - The row stays for a day after the last heartbeat, so "was it down last night?" can be answered the next morning.
  - A stopped instance holds no queue and is not counted live. `IsAlive` is false for it.
- The schema is at version 10. Migration 10:
  - adds the `owners` table and fills it from the existing jobs and summary, without names;
  - adds the index `jobs_owner (queue, owner, id)`. It is built inside the migration, about a second per million jobs, and jobs cannot be written while it builds;
  - adds `instances.stopped_at`.
- An older instance writes no names, and for two minutes counts a stopped newer instance as live.

## [0.3.0-preview.1] - 2026-11-04

Operations: an emergency stop for a queue or one owner, temporary limits on rate budgets, budgets that follow the limit the API reports, and a log of who did what and why, in C# and over HTTP.

### Added

- An emergency stop on `ISyncLaneMonitor`: `PauseAsync(queue, owner?, reason, by, until?)` and `ResumeAsync`.
  - Nothing of a paused queue or owner is taken.
  - Jobs of it running on any instance are cut at once and handed back without using an attempt.
  - A checkpoint or heartbeat stops a job whose instance missed the announcement.
  - Planning goes on, so nothing is lost; the cut page runs again on resume.
  - `GetPausesAsync` lists the pauses in force. `GetOverviewAsync` and `GetFreshnessAsync` show them.
  - Code `SL3009`. New job outcome `paused`.
- An actions log: every pause, resume and repair is written with who asked (`by`), why and how many jobs it changed, in the same statement as the change. `GetActionsAsync` lists it for 400 days.
- Temporary budget limits on `ISyncLaneMonitor`: `LimitBudgetAsync(budget, key?, limit, per, reason, by, until?)`, `ClearBudgetLimitAsync` and `GetBudgetLimitsAsync`.
  - For "the API answers slowly, halve our rate for an hour", without a deploy. Every instance obeys from its next request.
  - A limit only lowers a budget, never raises it. It ends at `until` or when cleared, and the rate then climbs back as it learns.
  - Logged in the actions log. Code `SL3010`.
- A budget follows the limit the API reports.
  - A recipe names the header in `rate.headers.limit` with its window in `limitWindow` (seconds), or sets `limitFormat: "ietf"` for the IETF `RateLimit-Policy` field, which carries its own window.
  - A hand-written handler calls `RateBudgets.ReportLimitAsync(permit, limit, per)`.
  - While the API reports a limit it is the budget's ceiling in place of the declared one, up or down. A repeated value writes nothing.
  - `synclane init` fills in `RateLimit-Policy` and, for a bare limit header, writes a note asking for its window.
- `GetBudgetsAsync` shows each key's reported limit (`ReportedRate`) and the operator's limit on it (`LimitedTo`).
- `SyncLane.AspNetCore` maps `POST /pause`, `POST /resume`, `GET /pauses`, `GET /actions`, `POST /budgets/limit`, `POST /budgets/clear-limit` and `GET /budget-limits`, and passes the signed-in user as `by`.

### Changed

- The repairs on `ISyncLaneMonitor` take an optional `by` before the cancellation token.
- A declared budget limit is no longer the ceiling once the API reports its own: the reported one takes its place, higher or lower. Readings of how many requests are left still only lower a budget.
- The schema is at version 9. Migration 9 adds the `pauses`, `actions` and `budget_limits` tables, and `reported_rate` and `reported_at` to `rate_buckets`. An instance of an older version does not obey pauses or limits.
- Clean-up also deletes pauses and budget limits that have ended.

## [0.2.0-preview.2] - 2026-11-04

The monitor: what every queue is doing, whose data is old and why, failures grouped by code, repairs, and a live stream of job changes, in C# and over HTTP with the new `SyncLane.AspNetCore`. Idle workers wake as soon as work arrives.

### Added

- The packages have an icon: an S drawn as a lane, in `assets/`. The README shows it too.
- `ISyncLaneMonitor`, registered by `AddSyncLane`, answers what an operator asks for every queue in the schema, declared in this application or not:
  - `GetOverviewAsync`: per queue, open jobs by state, owners, live instances, and the last 24 hours;
  - `GetFreshnessAsync`: owners ranked by how old their completed data is, with the job that explains it;
  - `GetProblemsAsync`: failing and abandoned jobs grouped by error code;
  - `GetBudgetsAsync`, `GetInstancesAsync`, and `GetVolumeAsync` per hour or day.

  Each is one round trip that reads summaries and open jobs, never history.
- Repairs on `ISyncLaneMonitor`, for any queue in the schema; none touches a running job:
  - `RequeueJobAsync(id)` and `RestartJobAsync(id)`, the second from the first page and for completed jobs too;
  - `RequeueByCodeAsync(queue, code)`;
  - `PrioritizeOwnerAsync(queue, owner)`, which moves an owner's waiting jobs ahead of the others.

  Codes `SL3008` and `SL3102`.
- `ISyncLaneMonitor.WatchAsync(queue?, owner?)` streams jobs as they are created and change status, from every instance, a moment after each change commits. A trigger announces every change with `NOTIFY` (migration 8), and one connection per process listens, reconnecting after a break.

- `SyncLane.AspNetCore`, a new package: `app.MapSyncLane("/synclane")` maps the monitor's reads, repairs and live stream (server-sent events) as Minimal API endpoints, listed in the host's OpenAPI document, and returns the group for `.RequireAuthorization(...)`.
  - Until the group requires authorization, the endpoints answer only requests from the same machine that did not come through a proxy.
  - Repairs need the `X-SyncLane-Action` header; `ReadOnly` leaves them out.
  - Answers are never cached and never show a job's cursor.
  - Codes `SL7001` and `SL7002`.

### Changed

- An idle worker slot wakes as soon as a job of its queue is created, requeued or handed back, on any instance, instead of waiting for `PollInterval`. Behind a pooler in transaction mode it polls as before.
- The schema is at version 8. Both new migrations only add. With `CreateSchema` off, run `SyncLaneSchema.Script()` before starting this version.
  - Migration 7, for the monitor:
    - `coverage` records how far each owner's completed data reaches;
    - a new `job_stats` table counts completed jobs, records, failed attempts and abandoned jobs per queue, type and hour, kept for 400 days;
    - a new index on initial jobs makes `GetProgressAsync` independent of an owner's recurring history.

    The statements that complete and settle a job keep these, and they are filled from existing jobs once.
  - Migration 8: the trigger that announces job changes.

## [0.2.0-preview.1] - 2026-11-04

The first version on NuGet: `SyncLane`, `SyncLane.Http` and the `synclane` tool, `SyncLane.Cli`. It also holds everything listed under 0.1.0 in CHANGELOG.md, which was never published. It stays a preview until the HTTP source has met the APIs of a production integration.

### Added

- Packages on NuGet with Source Link and symbol packages, so a debugger steps into the exact source of a release. A release is published from a version tag by a workflow that runs the PostgreSQL tests first and holds no API key.

- `SyncLane.Http`: the recipe model. `HttpRecipe.Parse` reads a recipe from JSON and reports every problem at once with codes `SL5001` to `SL5011`; placeholders `{{window.start}}`, `{{window.end}}` and `{{owner.key}}` are escaped for where they land; a recipe can only call the host it names. Requests are built but not sent yet. See `docs/recipes.md`.
- `SyncLane.Http`: recipes run. `HttpRecipeHandler` sends the request, maps answers to outcomes (429 waits, 401/403 and other 4xx abandon, 408/5xx/timeouts/network errors retry), reads at most `MaxResponseBytes`, and hands the records to an `IRecordSink`. `AddSyncLaneHttp` throws every problem at once; a start-up check stops the host when recipes and declared job types do not match. Codes `SL5012` to `SL5016` and `SL5105` to `SL5113`.
- The HTTP client follows no redirects, accepts gzip, deflate and brotli, and logs no URLs.
- `SyncLane.Http`: pagination by page number, offset, cursor (from the answer or the last record, with an optional has-more flag), next URL in the body, and `Link` header. The job's cursor holds the paging state, so a job continues from the page it reached. Next URLs must keep the recipe's scheme, host and port; a page pointing to itself and more than `MaxPagesPerJob` numbered pages stop the job. Codes `SL5017` and `SL5114` to `SL5118`.
- `SyncLane.Http`: authentication. A recipe's `auth` names a credential and never holds one; `ICredentialStore` gives each owner's values page by page. Types `bearer`, `basic`, `apiKey` (header or query) and `oauth2` (client credentials and refresh token: tokens kept in memory, renewed before they expire, shared by pages that need one at once, renewed once after a 401, rotated refresh tokens saved), and `signed` with an `IRequestSigner` that may not move the request. An API key the API copies into its next-page URLs is taken out before the URL becomes the cursor. `Authorization` in `request.headers` is refused. Codes `SL5018` to `SL5021` and `SL5119` to `SL5124`. A test runs every type through the engine against an API that echoes every secret, and finds none in logs, spans, metrics or the database.
- `samples/LiveCheck`: five public APIs, one per paging style, synced through the engine with recipes only and compared with the totals the APIs report; an optional sixth checks a bearer token against a real API. Run by hand, not in CI. The first run passed 5 of 5.
- A README section on recipes, which is also the package page of `SyncLane.Http`.
- Rate budgets, designed from the documented limits of large APIs:
  - `lane.AddBudget(name, limit, per, burst)` declares a budget, shared by every queue of every instance.
  - Buckets live in a new table, `rate_buckets` (migration 2). Taking a token is one statement that locks the bucket's row.
  - `RateBudgets.WaitAsync` takes a token per request and returns a `BudgetPermit`. `RateBudgets.ThrottledAsync(permit, retryAfter)` pauses the budget after a `429`, for `Retry-After` or a backoff, for every job on the key on every instance.
  - A `429` for a request sent before the latest pause began does not start another pause, so requests in flight together do not double the backoff once each.
  - Recipes draw from a budget with `"rate": { "budget": ..., "key": ... }`.
  - A wait longer than `MaxBudgetWait` postpones the job instead of holding its worker.
  - Metrics `synclane.budget.wait` and `synclane.budget.paused`, tagged with the budget's name only.
  - A recipe's `rate.headers` names where the API says how many requests are left and when more come (`remaining` as a number, `used/limit` or the IETF draft's `RateLimit` field; `reset` in seconds, Unix seconds or milliseconds, or an HTTP date). Readings only lower the budget, count requests still in flight, and with nothing left pause the key until the reset on every instance. Hand-written handlers use `RateBudgets.ReportRemainingAsync`. New metric `synclane.budget.exhausted`.
  - A `429` without `Retry-After` waits for the reset the recipe's headers give, at least a second.
  - Every budget learns its rate under the declared limit. A fresh `429` lowers it to 0.7 times itself; it climbs back per token while the budget is the bottleneck, quickly up to 90% of the rate that was refused and slowly beyond. `AddBudget(..., start)` starts a budget at a share of its limit, for limits nobody publishes. Against simulated APIs with hidden limits: under 1% `429`, 95 to 100% of the limit.
  - Codes `SL1008`, `SL1009`, `SL2004`, `SL3005`, `SL4003`, `SL5022` and `SL5023`.
- `WriteToPostgres(table)` on a recipe: SyncLane creates the table and keeps one row per account and record, written one statement per page. A repeated page changes nothing; with `VersionedBy`, an older copy never replaces a newer one. Records are keyed by a declared path, by `$.id` when every record has a unique one, or by their content; a page that cannot be keyed stops the job and names the fields that could serve. `PostgresSinkSchema.Script` for teams that apply migrations themselves. Codes `SL5024`, `SL5125`, `SL5126` and `SL5127`.
- `WriteToHttp(url)` on a recipe: each page is posted to a receiver as a Standard Webhooks message holding its records with their keys and versions. The same records always carry the same `webhook-id` and `Idempotency-Key`, since the body holds no time. A `whsec_` secret signs every request. Requests over `MaxBytes`, or refused with `413`, are split in halves. Failures are tried three times in the page without fetching the source again, and a receiver that asks to slow down postpones the job without using an attempt. Codes `SL5128` to `SL5130`.
- A JSON Schema for recipes, `schemas/recipe.schema.json`, also `HttpRecipe.JsonSchema`. It is generated from the recipe types with the descriptions of their documentation, so it cannot drift from what `Parse` reads. Editors complete fields, show descriptions, list each choice with what it does, and flag misspelt fields.
- `SinkConformance.CheckAsync(sink, read)`: checks a sink of your own against what workers do to it (a page written twice, a page fetched again with changes, duplicates in a page, an older copy after a newer one, the same key under two owners, an empty page), from any test framework. Code `SL5131`.
- The `synclane` command (`SyncLane.Cli`, a .NET tool):
  - `synclane init`, followed by a curl as copied or piped in, sends the request once and writes a recipe from the answer: records, paging, time window, rate headers, and notes on the key and version. Each inferred field has a comment saying why. The curl's credential moves into `auth` and is written nowhere.
  - `synclane test recipe.jsonc` runs a recipe for two pages through the real handler without a database. It reports fields, key uniqueness and whether page 2 brought new records, never record values, cursors or credentials. It exits with 1 when the recipe does not work.
  - Codes `SL6001` to `SL6011`.
- `RecordIdentity`, for sinks of your own: keys, versions and duplicates within a page, the same way the built-in destination reads them.
- `JobPostponedException`: a job gives its slot back without using an attempt and continues from its cursor after a delay. New outcome `postponed`.
- `SyncLaneOptions.Describe()`: a read-only list of the declared queues and job types with their handlers.
- `SyncLaneException.Errors`: an exception can carry several errors; `ThrowIfFailed()` throws all of them.
- A convention test that fails when a platform is named in the library's code.
- Old jobs are deleted. `KeepCompletedJobs` (default 7 days, `null` to keep all) sets how long completed recurring and manual jobs stay; instances that run the planner delete older ones every hour, in small batches, for their own queues only. Initial jobs and jobs that have not completed are kept.
- `ISyncLane.MarkLoadedAsync(queue, ranges)`: for moving from another system. History it already loaded is recorded as completed initial jobs, so SyncLane fetches only what is older and what came after. One bad range marks nothing. Codes `SL3006` and `SL3007`.
- `ISyncLane.ForgetOwnerAsync(queue, owner)`: deletes an owner's jobs and what the planner knows about it, for an account that is gone for good. Deleting its rows by hand would now leave the summary behind.

- Instances register in a new table, `instances` (migration 6), with their application, host, version, the queues they run and their budgets, and say they are alive every half minute. `options.Application` names the application, by default the host's application name. An instance that would run a queue a live instance of another application runs does not start (`SL1011`), so two applications sharing a schema cannot take each other's jobs. A budget declared differently elsewhere is logged as a warning. Code `SL1010` for a name that cannot be used.

### Changed

- A `403` that carries `Retry-After`, or whose declared remaining header says 0, is a throttle (`SL5105`) instead of refused credentials (`SL5106`). Some APIs answer a spent limit with `403`. The message of `SL5105` names the status it got.
- Attempts count failures since the job last saved a page: a saved page sets `attempt_count` back to 1. Until now they added up over a job's whole life, so a job of a thousand pages that met five timeouts or `429`s hours apart was deferred and then abandoned. `MaxAttempts` now limits failures in a row.
- With a rate budget, a `429` (or a `403` that means one) postpones the job for the budget's pause instead of using an attempt: the budget is what keeps the API safe. Without a budget it still uses one. `synclane.job.throttled` counts only the throttles that use an attempt.
- After a `429` without `Retry-After`, the pause is one token's time, at least a second, and doubles only while the first request after each pause is refused again. Until now it doubled with every `429` until a quiet spell, which drove pauses to five minutes while the rate was being lowered.
- The schema is at version 4: it adds the `rate_buckets` table with its `taken`, `rate`, `rate_max` and `taken_at_pause` columns, and changes nothing else. With `CreateSchema` on there is nothing to do. With it off, run `SyncLaneSchema.Script()` before starting this version; until then the host stops at start-up with `SL2001`. An instance of 0.1 keeps working against the new schema.
- The planner reads a summary, one row per queue, owner and type, instead of every job ever created (new table `coverage`, migration 5). The same statement that inserts jobs keeps it, so it can never disagree with them. Planning 200 owners took 1.7 ms with 10 periods each and 171 ms with 5,000; it now takes 0.2 ms either way.
- A recurring job that was abandoned is replaced by the next period's job, whose window reaches back over it. Until now a broken account added one abandoned row per period, and the windows they missed stayed missed after the cause was fixed. An abandoned history slice still waits for a requeue.
- The schema is at version 6. Migration 5 adds `coverage`, fills it from the existing jobs, and builds two indexes on `jobs`: `jobs_open` for the planner and `jobs_finished` for clean-up. On a large table, create the two indexes with `CREATE INDEX CONCURRENTLY` before upgrading (see `docs/operations.md`); the migration then skips them. Migration 6 adds the `instances` table. An older instance keeps working against the new schema.
- The README no longer suggests sharing a lane to share a rate limit: a lane runs one job at a time, a rate budget paces requests.
- `SL5109` (API unreachable) no longer keeps the `HttpRequestException` as its inner exception. Its message, which the engine stores in the `error` column, can hold the URL or a proxy's password; the error now says which kind of failure it was (host name, connection, TLS, proxy, protocol) instead.

## [0.1.0] - Not published

First version.

### Added

- One PostgreSQL table that holds every job with its queue, owner, lane, type, mode, status, window, cursor, counts, error and lease.
- Queues with their own concurrency, attempts, lease and delays.
- Snapshot and windowed job types, declared with their handler. Types run in the order they are declared.
- A planner that adds what is missing and can be repeated: history in slices back to a horizon, one recurring job per period, recurring windows that continue where coverage ends.
- Lanes: one running job per lane, enforced by a partial unique index.
- A page loop that saves the cursor after every page, renews the lease with a heartbeat and refuses writes from a worker that lost its job.
- Retries with a doubling delay, a longer delay for throttling, a deferred attempt once the lane is idle, and abandonment after that.
- Jobs handed back on shutdown and taken over after a crash, both from the cursor.
- `ISyncLane` to plan, enqueue, requeue, cancel, list and report progress.
- Demo: a pretend shop API, two handlers, one page and a Docker Compose file.
- Versioned migrations recorded in a `migrations` table, applied at start-up under a lock. A 0.1 table created before migrations existed is adopted as it is.
- `SyncLaneSchema.Script()` for teams that apply migrations themselves, and a start-up check that stops the host with a clear message when `CreateSchema` is off and a migration is missing.
- A "Your responsibility" section in the README.
- Metrics through `System.Diagnostics.Metrics` (meter `SyncLane`) and traces through `ActivitySource` (source `SyncLane`): jobs taken and finished by outcome, wait and run time, pages, records, throttles and planner runs. No new dependency; tags never name owners or lanes.

- `SyncResult`, `SyncResult<T>` and `SyncListResult<T>`: every `ISyncLane` call returns one, with typed errors instead of exceptions for expected failures.
- Stable error codes (`SyncErrorCodes`, `SL1001` to `SL4002`) with one message catalog, `SyncError`, `SyncErrorKind` with HTTP status mapping, and `SyncLaneException` for configuration errors. Every code is documented in `docs/errors.md`.
- Cursor pagination for `GetJobsAsync` (`JobQuery.Cursor`, `NextCursor`), with an optional total counted in the same round trip (`IncludeTotal`).
- `CLAUDE.md` with the rules for changing the code, `ARCHITECTURE.md` with the design and its decisions, and convention tests that enforce the checkable rules.

### Changed

- `ISyncLane` returns results: `EnqueueAsync` gives the new job id or `SL3101` instead of `false`; an undeclared queue is `SL3001` instead of an `ArgumentException`; a `Limit` out of range is `SL3003` instead of being clamped.
- Configuration errors throw `SyncLaneException` (an `InvalidOperationException`) carrying a code.
- Jobs abandoned by SyncLane itself have an `error` that starts with the code (`SL4001`, `SL4002`).
- Migrations missing from the database are applied in one statement, and queues are planned side by side within a round.
- Licensed under the Apache License 2.0 instead of MIT, for its explicit patent grant. The warranty disclaimer and limitation of liability are kept.
