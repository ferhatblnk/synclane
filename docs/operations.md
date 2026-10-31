# Operations

SyncLane has no admin tool because the table is one. This page collects the statements that answer the usual questions, and the settings that are easy to get wrong.

All examples use the default schema, `synclane`.

## Looking

What is running right now, and who has it:

```sql
SELECT id, owner, lane, type, cursor, processed_count, leased_by, heartbeat_at
FROM synclane.jobs
WHERE status = 'running'
ORDER BY started_at;
```

What is in trouble:

```sql
SELECT id, owner, type, status, attempt_count, next_run_at, error
FROM synclane.jobs
WHERE status IN ('failed', 'deferred', 'abandoned')
ORDER BY updated_at DESC;
```

How far each owner's first load is:

```sql
SELECT owner,
       count(*)                                     AS jobs,
       count(*) FILTER (WHERE status = 'completed') AS done,
       count(*) FILTER (WHERE status = 'abandoned') AS given_up
FROM synclane.jobs
WHERE mode = 'initial'
GROUP BY owner
ORDER BY owner;
```

Which range of an owner's orders is covered, and where the holes are:

```sql
SELECT window_start, window_end, mode, status, processed_count
FROM synclane.jobs
WHERE owner = 'globex' AND type = 'orders' AND window_start IS NOT NULL
ORDER BY window_start;
```

Why a lane is not moving. One of these rows is the answer: a job that is running, or one that is waiting for `next_run_at`:

```sql
SELECT id, type, status, attempt_count, next_run_at, lease_expires_at, leased_by, error
FROM synclane.jobs
WHERE lane = 'globex' AND status IN ('pending', 'running', 'failed', 'deferred')
ORDER BY priority, window_start DESC NULLS LAST;
```

Which rate budgets are paused, and until when. `generation` counts the pauses of a bucket; `last_pause` is the most recent one's length:

```sql
SELECT budget, key, refilled_at AS paused_until, generation, throttled_at, last_pause
FROM synclane.rate_buckets
WHERE refilled_at > now()
ORDER BY refilled_at DESC;
```

What each bucket has learned. `rate` is the current rate in requests a second, never above the declared limit; `rate_max` is the rate that last caused a `429`. A rate far below the declared limit means the API, or another app on the account, allows less than you declared:

```sql
SELECT budget, key, round(rate::numeric, 3) AS rate, round(rate_max::numeric, 3) AS rate_max, throttled_at
FROM synclane.rate_buckets
ORDER BY budget, rate;
```

## Changing

Everything `ISyncLane` does is a plain update, so you can do the same from `psql` when you need something it does not offer.

Give stopped jobs of one owner fresh attempts (this is `RequeueAsync`). The cursor is left alone on purpose:

```sql
UPDATE synclane.jobs
SET status = 'pending', attempt_count = 0, next_run_at = now(), error = NULL, completed_at = NULL, updated_at = now()
WHERE owner = 'globex' AND status IN ('failed', 'deferred', 'abandoned');
```

Start one job over from its first page. Only do this if the other side's cursor is no longer valid:

```sql
UPDATE synclane.jobs
SET cursor = NULL, processed_count = 0
WHERE id = 33 AND status <> 'running';
```

Hold an owner back for an hour without losing anything:

```sql
UPDATE synclane.jobs
SET next_run_at = now() + interval '1 hour'
WHERE owner = 'globex' AND status IN ('pending', 'failed');
```

Let one job jump the queue:

```sql
UPDATE synclane.jobs SET priority = -1 WHERE id = 33;
```

Do not set a row to `running` by hand, and do not change `leased_by`. Those two columns are how a worker proves a job is still its own.

## Keeping the table small

Completed rows are never deleted by SyncLane. How long you keep them is your decision, with one thing to know first:

**Initial rows are the record of what has been planned.** The planner works out what is missing by looking at them. If you delete the initial rows of an owner, the next planning round sees a new owner and plans the whole history again.

Recurring rows are different. Only the newest one per owner and type matters to the planner, because it marks where coverage ends. Older ones are history you may or may not want to keep:

```sql
DELETE FROM synclane.jobs AS old
WHERE old.mode = 'recurring'
  AND old.status = 'completed'
  AND old.completed_at < now() - interval '30 days'
  AND EXISTS (
      SELECT 1
      FROM synclane.jobs AS newer
      WHERE newer.queue = old.queue AND newer.owner = old.owner AND newer.type = old.type
        AND newer.mode = 'recurring' AND newer.id > old.id);
```

`rate_buckets` holds one small row per budget and key, updated in place. Rows of keys you no longer use can be deleted; a key that is used again starts with a full bucket.

When an account is removed for good, delete all its rows. When it only disconnects, call `CancelAsync` and stop returning it from your `IOwnerSource`. If it connects again, call `RequeueAsync` for it: its abandoned slices are still planned and come back with their cursors.

## Choosing the numbers

**`slice`** is the setting with the most consequences. One slice is one job, and a job is the largest unit of work that cannot be split. Work out how long one slice takes for your biggest account at the other side's rate limit.

The system this design comes from once tripled its slice from 30 to 90 days to "have fewer jobs". For the largest account a single slice then held about 43,000 records, 430 pages, roughly twenty hours of requests, against page tokens that lived for twenty-four. Every hiccup late in a slice cost most of a day. Small slices finish, and a finished slice is progress that cannot be lost.

**`LeaseDuration`** only needs to cover the time it takes to notice a dead process, because the heartbeat renews it during long pages. The default of five minutes means a job whose worker died waits at most five minutes to be taken over. Going much lower mostly buys false alarms during garbage collection pauses or a slow database.

**`Concurrency`** is per process and per queue. Three instances with `Concurrency = 4` run up to twelve jobs of that queue at once, and still at most one per lane. Set it to `1` for an API that must be called strictly one request at a time, whatever the account.

**`MaxAttempts`** counts every pick-up, including ones that ended in a throttle. With the defaults a job that keeps failing is deferred after about fifteen minutes of ordinary failures, or more than an hour of throttling.

**`every` and `overlap`**: a recurring window starts `overlap` before the previous one ended. Use the overlap to cover records that show up late on the other side, and rely on upserts to make the double read harmless.

## Metrics and traces

All metrics belong to the meter `SyncLane` (`SyncLaneTelemetry.MeterName`). Every one carries `synclane.queue` and, except the planner metrics, `synclane.job.type`. Owners and lanes are never tags: there can be thousands of them and they often identify customers. Use the table for questions about one owner.

| Metric | Unit | What it counts |
| --- | --- | --- |
| `synclane.job.taken` | `{job}` | Jobs a worker took. |
| `synclane.job.wait` | `s` | Time between a job becoming due and a worker taking it. Rising wait with idle CPU means too few worker slots, or lanes that are always busy. |
| `synclane.job.finished` | `{job}` | Runs that ended, with `synclane.job.outcome`: `completed`, `failed`, `deferred`, `abandoned`, `released` (worker stopping), `postponed` (waiting, attempt not counted) or `lease_lost`. |
| `synclane.job.duration` | `s` | Time from taking a job to the end of the run, with the same outcome tag. |
| `synclane.page.completed` | `{page}` | Pages a handler returned. |
| `synclane.page.duration` | `s` | Time a handler took for a page it returned. |
| `synclane.record.processed` | `{record}` | Records the handlers reported. |
| `synclane.job.throttled` | `{job}` | Runs that ended with a `JobThrottledException`. They are also counted as `failed`, `deferred` or `abandoned`. |
| `synclane.planner.planned` | `{job}` | Jobs the planner added. |
| `synclane.planner.failed` | `{run}` | Planner runs that failed. |
| `synclane.budget.wait` | `s` | Time a request waited for a token of a rate budget. Tagged `synclane.budget` only; the key is an owner and never a tag. |
| `synclane.budget.paused` | `{pause}` | Times a rate budget was paused because the API answered `429`. Tagged `synclane.budget` only. |
| `synclane.budget.exhausted` | `{pause}` | Times a rate budget waited for a reset because the API's headers said nothing was left. Tagged `synclane.budget` only. Expected when a quota is used to the full. |

Traces come from the activity source `SyncLane` (`SyncLaneTelemetry.ActivitySourceName`). A `synclane.job` span covers one run of a job and carries `synclane.job.id`, `synclane.job.mode`, `synclane.job.attempt` and, at the end, `synclane.job.outcome`; a failed run records the exception and sets the span status to error. Each page is a `synclane.page` span inside it with `synclane.page.processed`. The cursor is not recorded, because some APIs put tokens in it.

Four signals worth an alert:

- `synclane.job.finished{outcome="abandoned"}` above zero. Something waits for a person.
- `synclane.job.throttled` growing faster than `synclane.job.taken`. The pace is too high for the other side.
- `synclane.job.finished{outcome="lease_lost"}` above zero. A worker stalled longer than `LeaseDuration`, or clocks disagree.
- `synclane.budget.paused` growing steadily. The declared budget is above what the API allows now: lower it. Another app on the same account can cause this too. `synclane.budget.exhausted` growing is not a problem: it means SyncLane stopped at zero instead of being throttled.

## Several instances

Run as many instances as you like against the same table. Planning is safe to do from all of them, and so is taking jobs.

If your web instances should not do background work, set `RunWorkers = false` and `RunPlanner = false` there. `ISyncLane` works the same either way.

Rate budgets are shared through `rate_buckets`: every instance takes its tokens from the same row, and a `429` heard by one instance pauses the key on all of them. Declare the same budgets on every instance that runs workers. Taking a token is one statement that locks the bucket's row for a moment; measured against a local PostgreSQL 18, one key served about 12,000 tokens a second to eight callers, far more than any API we know allows.

Keep the clocks of your instances in sync. Leases, due times and token refills are computed with the clock of the instance that asks. For leases a skew of seconds does not matter; for a budget, an instance whose clock runs one second ahead can let one second's worth of extra tokens through.

## Upgrading the schema

The schema is a numbered list of migrations. `synclane.migrations` records which ones a database has. With `CreateSchema` on, each instance applies what is missing at start-up, in one transaction, behind a lock; instances that start together wait for each other, and the second one finds nothing to do.

Migrations are only ever added, and each one only adds. An instance still running the previous version keeps working against the upgraded table, so you can roll a deploy one instance at a time.

A table created by SyncLane 0.1, before the migrations table existed, is adopted as it is: migration 1 finds the table and indexes in place and only records itself.

## Running the schema yourself

Set `CreateSchema = false` and put the script into your own migrations. `SyncLaneSchema.Script()` returns it for any schema name; with EF Core that is one line:

```csharp
migrationBuilder.Sql(SyncLaneSchema.Script("synclane"));
```

When a SyncLane upgrade brings a new migration, add a new migration of your own with the same line. The script is safe to run on a database that already has some or all of it.

[schema.sql](schema.sql) is the same script for the default schema, checked against the library by a test.

With `CreateSchema` off SyncLane never changes the database. It checks at start-up that every migration it knows is recorded, and if one is missing it stops the host with a message that names the migration and the call that produces it.
