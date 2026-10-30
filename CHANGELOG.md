# Changelog

All notable changes to this project are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [0.2.0] - Unreleased

### Added

- `SyncLane.Http`, phase 1.1: the recipe model. `HttpRecipe.Parse` reads a recipe from JSON and reports every problem at once with codes `SL5001` to `SL5011`; placeholders `{{window.start}}`, `{{window.end}}` and `{{owner.key}}` are escaped for where they land; a recipe can only call the host it names. Requests are built but not sent yet. See `docs/recipes.md`.
- A convention test that fails when a platform is named in the library's code.
- `docs/design/phase-1.md`: the parts of phase 1, their gates and the checks against refuted ideas.

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
