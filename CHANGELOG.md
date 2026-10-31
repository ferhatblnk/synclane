# Changelog

All notable changes to this project are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [0.2.0] - Unreleased

Packed as `0.2.0-preview.1` until the HTTP source has met the APIs of a production integration.

### Added

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
- `JobPostponedException`: a job gives its slot back without using an attempt and continues from its cursor after a delay. New outcome `postponed`.
- `SyncLaneOptions.Describe()`: a read-only list of the declared queues and job types with their handlers.
- `SyncLaneException.Errors`: an exception can carry several errors; `ThrowIfFailed()` throws all of them.
- A convention test that fails when a platform is named in the library's code.

### Changed

- A `403` that carries `Retry-After`, or whose declared remaining header says 0, is a throttle (`SL5105`) instead of refused credentials (`SL5106`). Some APIs answer a spent limit with `403`. The message of `SL5105` names the status it got.
- Attempts count failures since the job last saved a page: a saved page sets `attempt_count` back to 1. Until now they added up over a job's whole life, so a job of a thousand pages that met five timeouts or `429`s hours apart was deferred and then abandoned. `MaxAttempts` now limits failures in a row.
- With a rate budget, a `429` (or a `403` that means one) postpones the job for the budget's pause instead of using an attempt: the budget is what keeps the API safe. Without a budget it still uses one. `synclane.job.throttled` counts only the throttles that use an attempt.
- After a `429` without `Retry-After`, the pause is one token's time, at least a second, and doubles only while the first request after each pause is refused again. Until now it doubled with every `429` until a quiet spell, which drove pauses to five minutes while the rate was being lowered.
- The schema is at version 4: it adds the `rate_buckets` table with its `taken`, `rate`, `rate_max` and `taken_at_pause` columns, and changes nothing else. With `CreateSchema` on there is nothing to do. With it off, run `SyncLaneSchema.Script()` before starting this version; until then the host stops at start-up with `SL2001`. An instance of 0.1 keeps working against the new schema.
- The README no longer suggests sharing a lane to share a rate limit: a lane runs one job at a time, a rate budget paces requests.
- `SL5109` (API unreachable) no longer keeps the `HttpRequestException` as its inner exception. Its message, which the engine stores in the `error` column, can hold the URL or a proxy's password; the error now says which kind of failure it was (host name, connection, TLS, proxy, protocol) instead.

## [0.1.0] - Unreleased

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
