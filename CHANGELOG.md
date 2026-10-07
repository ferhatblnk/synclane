# Changelog

All notable changes to this project are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

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
