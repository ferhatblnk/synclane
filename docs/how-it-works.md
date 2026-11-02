# How it works

SyncLane is four pieces around one table: a planner that inserts rows, worker slots that take them, a runner that drives your handler, and a handful of statements that move a row from one status to the next. This page goes through them in the order a job meets them.

## The table

Everything is in `synclane.jobs`. The full definition is in [schema.sql](schema.sql); `synclane.migrations` beside it only records which schema version the database has.

| Column | Meaning |
| --- | --- |
| `queue` | The queue. Each queue has its own worker slots and settings. |
| `owner` | Who the job works for: one connected account. |
| `lane` | Jobs with the same lane never run at the same time. Defaults to the owner. |
| `type` | The job type, which selects the handler. |
| `mode` | `initial` (first load), `recurring` (periodic refresh) or `manual` (enqueued by hand). |
| `status` | `pending`, `running`, `completed`, `failed`, `deferred` or `abandoned`. |
| `priority` | Lower runs first. It is the position of the type's declaration. |
| `window_start`, `window_end` | The range the job covers, if it has one. |
| `dedup_key` | What makes the job unique for its owner and type. |
| `cursor` | Where the next page starts. |
| `processed_count` | Records handled so far, over all pages and attempts. |
| `attempt_count` | How many times a worker has picked the job up since it last saved a page. A saved page sets it back to 1. |
| `error` | The last failure. |
| `next_run_at` | The job is not picked up before this moment. |
| `leased_by`, `lease_expires_at` | Who holds the job and until when. |
| `created_at`, `started_at`, `heartbeat_at`, `completed_at`, `updated_at` | The life of the row. |

Status and mode are stored as text, so the table reads without a legend.

## Planning

Covered in [planning.md](planning.md). In short: one query reads what exists for all owners, a pure function works out what is missing, one insert adds it.

## Taking a job

A worker slot takes its next job with one statement:

```sql
UPDATE synclane.jobs AS j
SET status = 'running',
    leased_by = @worker,
    lease_expires_at = @lease_expires_at,
    attempt_count = j.attempt_count + 1,
    started_at = COALESCE(j.started_at, @now),
    heartbeat_at = @now,
    updated_at = @now
WHERE j.id = (
    SELECT c.id
    FROM synclane.jobs AS c
    WHERE c.queue = @queue
      AND c.next_run_at <= @now
      AND (c.status IN ('pending', 'failed')
           OR (c.status = 'running' AND c.lease_expires_at <= @now)
           OR (c.status = 'deferred' AND NOT EXISTS (
                   SELECT 1
                   FROM synclane.jobs AS w
                   WHERE w.queue = c.queue AND w.lane = c.lane AND w.id <> c.id
                     AND w.status IN ('pending', 'running', 'failed'))))
      AND NOT EXISTS (
          SELECT 1
          FROM synclane.jobs AS r
          WHERE r.queue = c.queue AND r.lane = c.lane AND r.id <> c.id
            AND r.status = 'running')
    ORDER BY (c.status = 'deferred'), c.priority, c.window_start DESC NULLS LAST, c.next_run_at, c.id
    LIMIT 1
    FOR UPDATE OF c SKIP LOCKED)
RETURNING j.*;
```

The statement in `JobSql.cs` is this one with the schema name filled in and the returned columns written out. Reading it from the inside out:

- **Who is a candidate.** A job that is due and is `pending`, `failed`, `running` with a lease that ran out, or `deferred` while nothing else is waiting in its lane.
- **The lane rule.** A candidate is skipped while another job of its lane is `running`. Note that this does not ask whether that other job's lease is still alive. A lane with a dead worker stays closed until that same job is taken over; only then does the lane move on. This is deliberate, see [The lane is closed by the row, not by the lease](#the-lane-is-closed-by-the-row-not-by-the-lease).
- **The order.** Deferred jobs last. Then by type, in the order the types were declared. Then the newest window first, so a fresh recurring window runs before older slices of history. Then by due time.
- **`SKIP LOCKED`.** Two slots asking at once do not wait for each other; each gets a different row.

### Why the index is needed on top

`SKIP LOCKED` keeps two slots off the same row. It does not keep them off the same lane. Slot A picks job 1 of lane X; slot B, in the same instant, skips job 1 because it is locked, and picks job 2 of lane X. Neither sees a `running` job in lane X yet, because neither has committed.

The table has this index:

```sql
CREATE UNIQUE INDEX jobs_one_running_per_lane
    ON synclane.jobs (queue, lane) WHERE status = 'running';
```

With it, the database refuses the second update with a unique violation. SyncLane catches that and asks again; this time the lane is visibly busy and another lane is chosen. There is no lock to acquire and none to forget to release.

Measured on a laptop: 24 workers, 3 lanes, 600 jobs. Without the index, two jobs of one lane were at work together 170 times. With it, never. The test `Many_workers_never_put_two_jobs_of_one_lane_to_work` keeps that true.

## Running a job

The runner calls your handler with the job, saves what it returns, and calls it again:

```
repeat
    page = handler.RunPageAsync(job)             # a new service scope for every page
    save cursor = page.NextCursor,
         processed_count += page.Processed,
         renew the lease
until page.NextCursor is null
mark completed
```

Three things happen around that loop.

**A heartbeat.** While the handler is inside a page, a timer renews the lease every third of `LeaseDuration`. The lease therefore only has to be long enough to notice a dead process. It does not have to be longer than your slowest page.

**Fencing.** Every write the runner makes to a job ends with `WHERE id = @id AND status = 'running' AND leased_by = @worker`. If the lease ran out and another worker took the job, those writes change nothing, the old worker notices and stops. It cannot overwrite the new holder's cursor.

**A new scope per page.** The handler is resolved from a fresh dependency-injection scope for each page, so a `DbContext` or anything else scoped does not accumulate state over a job with hundreds of pages.

## When an attempt fails

| What happened | New status | When it runs again |
| --- | --- | --- |
| Exception, attempts left | `failed` | After `RetryBaseDelay`, doubled per attempt, capped at `RetryMaxDelay` |
| `JobThrottledException` | `failed` | After the wait it named, or `ThrottleBaseDelay` doubled per attempt, capped at `ThrottleMaxDelay` |
| `JobPostponedException` | `pending`, the attempt not counted | After the wait it named. A rate budget throws it when a token is far away, and a recipe with a budget when the API says "slow down" |
| Exception on attempt `MaxAttempts` | `deferred` | When nothing else is pending, running or waiting for a retry in its lane |
| Exception on the deferred attempt | `abandoned` | After a requeue |
| `JobAbandonedException` | `abandoned` | After a requeue |
| No handler for the type | `abandoned` | After a requeue |

The cursor and the processed count are never reset by a failure. Whatever status a job leaves `running` with, it comes back at the page it stopped on.

**Attempts count failures since the last saved page.** A job that saves a page has shown that it works, so its count starts again. A job of a thousand pages that meets a timeout now and then, hours apart, finishes; a job that fails five times on the same page is deferred. Airbyte draws the same line: it limits attempts in a row that synced nothing, and lets attempts that made progress go on. Here progress is never lost, because the cursor is saved, so a job that keeps making progress always reaches the end.

### Why `deferred` exists

Without it there are two choices when a job runs out of attempts, and both are bad. Keep retrying, and one broken slice of history holds up everything behind it in the lane, including the fresh data. Give up at once, and a slice that only failed because the lane was busy and rate limited is lost until a human notices.

`deferred` takes the job out of the way, lets the rest of the lane finish, and then gives the job one attempt with the lane to itself. If it fails even then, it is abandoned and visible.

## When a worker stops or dies

**Stops** (deployment, scale-down): the host's stopping token cancels the handler, the runner writes the job back as `pending` with its cursor, and gives the attempt back, because being interrupted by your own deployment is not a failure.

**Dies** (out of memory, node gone): nobody renews the lease. Once `lease_expires_at` has passed, the job is a candidate again and another slot takes it over, from the cursor. The attempt counts. A job that has been picked up more often than `MaxAttempts + 1` without ever reporting a result is abandoned instead of being run again, so a job that takes its worker down every time does not do so forever.

### The lane is closed by the row, not by the lease

An earlier version of this design let a lane move on when the lease of its running job had expired. Then this happened: an account was being throttled, pages were taking around nine minutes each with all the waiting and retrying, the lease was ten minutes, and sooner or later one page ran past it. Another worker saw "nothing alive in this lane", took the account's next job and started calling the same rate-limited API in parallel. That produced more throttling, which made pages slower, which made more leases go stale.

Since then a lane is closed as long as it has a row in `running`, whatever its lease says. An expired lease allows exactly one thing: taking over that same job. Together with the heartbeat, a slow page no longer looks like a dead worker in the first place.

## What your handler has to do

- **Write idempotently.** A page can run twice: after a crash between your write and the cursor save, and in the overlap between two windows. Upsert by the other side's identifier.
- **Return a cursor you can resume from.** If the other side's page tokens expire, keep a slice small enough to finish well inside their lifetime. A slice is the largest unit of work that cannot be split.
- **Respect the cancellation token.** That is how a stopping worker gets its job back to the table quickly.
- **Say what kind of failure it was** when you know: `JobThrottledException` for "slow down", `JobAbandonedException` for "this will never work", `JobPostponedException` for "not a failure, only wait".
