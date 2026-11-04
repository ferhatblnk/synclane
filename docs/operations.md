# Operations

SyncLane has no admin tool because the table is one. This page collects the statements that answer the usual questions, and the settings that are easy to get wrong.

All examples use the default schema, `synclane`.

## Looking

`ISyncLaneMonitor` answers the usual questions without SQL: the state of each queue, whose data is old and why, failures grouped by error code, rate budgets, instances, and volume per hour or day. See [Watching it run](../README.md#watching-it-run). The queries below are for everything else.

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

What each bucket has learned. `rate` is the current rate in requests a second, never above the ceiling; `rate_max` is the rate that last caused a `429`; `reported_rate` is the limit the API last reported, which is the ceiling in place of the declared limit when it is there. A rate far below the ceiling means the API, or another app on the account, allows less than it says:

```sql
SELECT budget, key, round(rate::numeric, 3) AS rate, round(rate_max::numeric, 3) AS rate_max,
       round(reported_rate::numeric, 3) AS reported_rate, throttled_at
FROM synclane.rate_buckets
ORDER BY budget, rate;
```

## Changing

`ISyncLaneMonitor` has an emergency stop and the usual repairs, for any queue in the schema:

| Call | What it does |
| --- | --- |
| `PauseAsync(queue, owner?, reason, by, until?)` | Stops a queue, or one owner on it, at once. Running jobs of it are cut on every instance and handed back without using an attempt; nothing of it is taken until `ResumeAsync` or `until`. Planning goes on, and the next window reaches back over the pause, so nothing is lost. The page that was cut runs again on resume, which is why handlers write idempotently. |
| `ResumeAsync(queue, owner?, by)` | Lifts it. Idle workers wake at once. |
| `LimitBudgetAsync(budget, key?, limit, per, reason, by, until?)` | Lowers a rate budget, or one key of it, for a while: "the API answers slowly, halve our rate for an hour". Every instance obeys from its next request. It only ever lowers: the declared limit, or the one the API reports, stays the upper bound. When it ends, the rate climbs back as it learns. Permanent changes belong in the budget's declaration. |
| `ClearBudgetLimitAsync(budget, key?, by)` | Lifts it. |
| `RequeueJobAsync(id)` | Gives one failed, deferred or abandoned job fresh attempts, keeping its cursor. |
| `RestartJobAsync(id)` | Runs one job again from its first page, a completed one too, for a window whose data has to be fetched again. |
| `RequeueByCodeAsync(queue, code)` | Requeues every failing, deferred and abandoned job of the queue with that error code, after you fixed the cause. |
| `PrioritizeOwnerAsync(queue, owner)` | Moves the owner's waiting jobs ahead of every other owner's, keeping its own type order. |

Apart from the pause, none of them touches a running job. A call about one job that cannot act says why (`SL3008`, `SL3102`); the others return how many jobs they changed.

Every one of them is written to the `actions` table with who asked (`by`, which the HTTP endpoints fill in from the signed-in user), why, and how many jobs it changed. `GetActionsAsync` lists them, newest first, for 400 days. `GetPausesAsync` lists the pauses in force; `GetOverviewAsync` and `GetFreshnessAsync` show them too. `GetBudgetLimitsAsync` lists the budget limits in force, and `GetBudgetsAsync` shows on each key the limit it obeys.

An instance of a version before pauses keeps taking jobs of a paused queue, and paces at the declared limit whatever an operator or the API sets. Upgrade every instance before relying on either.

Everything SyncLane does is a plain update, so you can do the same from `psql` when you need something it does not offer.

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

SyncLane deletes old jobs itself. Instances that run the planner do it at start-up and then every hour, in batches of 1,000, for their own queues only:

| Kept | Deleted |
| --- | --- |
| Initial jobs. They are bounded by the horizon, `GetProgressAsync` counts them, and they record what history was loaded. | Completed recurring and manual jobs older than `KeepCompletedJobs` (default 7 days). |
| Jobs that have not completed: pending, running, failed, deferred and abandoned. | |

Set `KeepCompletedJobs = null` to keep every job. Deleting is safe at any age: the planner reads `coverage`, not the history.

An abandoned recurring job does not pile up: the next period's job replaces it and reaches back over its window. An abandoned slice of history waits for `RequeueAsync`.

`rate_buckets` holds one small row per budget and key, updated in place. Rows of keys you no longer use can be deleted; a key that is used again starts with a full bucket.

When an account only disconnects, call `CancelAsync` and stop returning it from your `IOwnerSource`. If it connects again, call `RequeueAsync` for it: its abandoned slices are still planned and come back with their cursors.

When an account is removed for good, stop returning it from your `IOwnerSource`, then call `ForgetOwnerAsync`, then delete its data. A job running at that moment stops at its next page, and that page may still reach your handler. Do not delete its rows by hand: the summary would stay, and if the account came back its history would never be loaded again.

## Moving from another system

If another system already loaded an account's history, record it with `MarkLoadedAsync` before the planner runs for that account. Each range becomes a completed initial job. The planner then fetches only what is older, back to the horizon, and what came after, with the next recurring window.

```csharp
var ranges = oldJobs
    .GroupBy(job => (job.StoreId, job.Type))
    .Select(group => new LoadedRange
    {
        Owner = new JobOwner(group.Key.StoreId.ToString()),
        Type = group.Key.Type,
        Start = group.Min(job => job.WindowStart),
        End = group.Max(job => job.WindowEnd)
    })
    .ToList();

var result = await syncLane.MarkLoadedAsync("orders", ranges);
result.ThrowIfFailed();
```

Mark only ranges without holes: a hole inside a marked range is never fetched. If the old system's history has gaps, mark each unbroken run as its own range.

- It is safe to run twice: the same range is recorded once.
- One bad range marks nothing, and every problem is returned at once (`SL3006`, `SL3007`).
- Only windowed types can be marked. A snapshot type reads everything on each run anyway.
- A range must end by now. For a type without `every` it must reach today (UTC), because such a type never loads what came after its history.

A safe order:
1. Deploy with `RunPlanner` and `RunWorkers` off.
2. Mark the history.
3. Stop the old system.
4. Turn SyncLane on.

## Choosing the numbers

**`slice`** is the setting with the most consequences. One slice is one job, and a job is the largest unit of work that cannot be split. Work out how long one slice takes for your biggest account at the other side's rate limit.

The system this design comes from once tripled its slice from 30 to 90 days to "have fewer jobs". For the largest account a single slice then held about 43,000 records, 430 pages, roughly twenty hours of requests, against page tokens that lived for twenty-four. Every hiccup late in a slice cost most of a day. Small slices finish, and a finished slice is progress that cannot be lost.

**`LeaseDuration`** only needs to cover the time it takes to notice a dead process, because the heartbeat renews it during long pages. The default of five minutes means a job whose worker died waits at most five minutes to be taken over. Going much lower mostly buys false alarms during garbage collection pauses or a slow database.

**`Concurrency`** is per process and per queue. Three instances with `Concurrency = 4` run up to twelve jobs of that queue at once, and still at most one per lane. Set it to `1` for an API that must be called strictly one request at a time, whatever the account.

**`MaxAttempts`** counts pick-ups since the job last saved a page, so it limits failures in a row, not over a job's life. A throttle counts only when the recipe or handler has no rate budget; with one, the budget's pause is what protects the API, and the job is postponed instead. With the defaults a job that keeps failing on one page is deferred after about fifteen minutes of ordinary failures, or more than an hour of unbudgeted throttling.

A job whose budget keeps being refused waits as long as the budget's pause, which grows to five minutes, and is never deferred for it: it shows as `pending` with an `SL5105` error, and `synclane.budget.paused` grows. That is the signal to look at the account.

**`every` and `overlap`**: a recurring window starts `overlap` before the previous one ended. Use the overlap to cover records that show up late on the other side, and rely on upserts to make the double read harmless.

## Metrics and traces

All metrics belong to the meter `SyncLane` (`SyncLaneTelemetry.MeterName`). Every one carries `synclane.queue` and, except the planner metrics, `synclane.job.type`. Owners and lanes are never tags: there can be thousands of them and they often identify customers. Use the table for questions about one owner.

| Metric | Unit | What it counts |
| --- | --- | --- |
| `synclane.job.taken` | `{job}` | Jobs a worker took. |
| `synclane.job.wait` | `s` | Time between a job becoming due and a worker taking it. Rising wait with idle CPU means too few worker slots, or lanes that are always busy. |
| `synclane.job.finished` | `{job}` | Runs that ended, with `synclane.job.outcome`: `completed`, `failed`, `deferred`, `abandoned`, `released` (worker stopping), `postponed` (waiting, attempt not counted), `paused` (cut by a pause, attempt not counted) or `lease_lost`. |
| `synclane.job.duration` | `s` | Time from taking a job to the end of the run, with the same outcome tag. |
| `synclane.page.completed` | `{page}` | Pages a handler returned. |
| `synclane.page.duration` | `s` | Time a handler took for a page it returned. |
| `synclane.record.processed` | `{record}` | Records the handlers reported. |
| `synclane.job.throttled` | `{job}` | Runs that ended with a `JobThrottledException`: throttles without a rate budget, which use an attempt. They are also counted as `failed`, `deferred` or `abandoned`. With a budget, a throttle postpones the job and shows in `synclane.budget.paused`. |
| `synclane.planner.planned` | `{job}` | Jobs the planner added. |
| `synclane.planner.failed` | `{run}` | Planner runs that failed. |
| `synclane.budget.wait` | `s` | Time a request waited for a token of a rate budget. Tagged `synclane.budget` only; the key is an owner and never a tag. |
| `synclane.budget.paused` | `{pause}` | Times a rate budget was paused because the API answered `429`. Tagged `synclane.budget` only. |
| `synclane.budget.exhausted` | `{pause}` | Times a rate budget waited for a reset because the API's headers said nothing was left. Tagged `synclane.budget` only. Expected when a quota is used to the full. |

Traces come from the activity source `SyncLane` (`SyncLaneTelemetry.ActivitySourceName`). A `synclane.job` span covers one run of a job and carries `synclane.job.id`, `synclane.job.mode`, `synclane.job.attempt` and, at the end, `synclane.job.outcome`; a failed run records the exception and sets the span status to error. Each page is a `synclane.page` span inside it with `synclane.page.processed`. The cursor is not recorded, because some APIs put tokens in it.

Four signals worth an alert:

- `synclane.job.finished{outcome="abandoned"}` above zero. Something waits for a person.
- `synclane.job.throttled` growing faster than `synclane.job.taken`. The pace is too high for the other side: give the recipe or handler a rate budget.
- `synclane.job.finished{outcome="lease_lost"}` above zero. A worker stalled longer than `LeaseDuration`, or clocks disagree.
- `synclane.budget.paused` growing steadily. The declared budget is above what the API allows now: lower it. Another app on the same account can cause this too. `synclane.budget.exhausted` growing is not a problem: it means SyncLane stopped at zero instead of being throttled.

## Several instances

Run as many instances as you like against the same table. Planning is safe to do from all of them, and so is taking jobs.

If your web instances should not do background work, set `RunWorkers = false` and `RunPlanner = false` there. `ISyncLane` works the same either way.

Rate budgets are shared through `rate_buckets`: every instance takes its tokens from the same row, and a `429` heard by one instance pauses the key on all of them. Declare the same budgets on every instance that runs workers. Taking a token is one statement that locks the bucket's row for a moment; measured against a local PostgreSQL 18, one key served about 12,000 tokens a second to eight callers, far more than any API we know allows.

Keep the clocks of your instances in sync. Leases, due times and token refills are computed with the clock of the instance that asks. For leases a skew of seconds does not matter; for a budget, an instance whose clock runs one second ahead can let one second's worth of extra tokens through.

## Several applications on one schema

Applications can share one database and one SyncLane schema. Each declares its own queues, so jobs, planning and clean-up stay separate, and a budget declared by several applications is shared by all of them. One table then shows every sync of every application.

Each process registers in `instances` at start-up and every half minute after. Its row holds:
- its application;
- its host;
- the SyncLane version;
- the queues it runs with its planner or workers;
- its budgets.

The application is `options.Application`. It defaults to the host's application name, which is the entry assembly's unless the host sets another. Copies of one application share their queues.

**A queue belongs to one application at a time.** A worker takes any job of a queue it declares, so two applications on one queue would take each other's jobs and abandon the types they do not know (`SL4001`). An instance that would run a queue a live instance of another application runs does not start, and says which one (`SL1011`). Rename the queue in one of them, or give both the same `Application` if they are one application.

- An instance that runs neither the planner nor workers claims no queue. It can declare any queue to call `ISyncLane` on it, for example from an admin application.
- An instance removes its row when it stops. One that dies without stopping counts as gone two minutes after its last heartbeat, and its row is deleted after a day.
- A budget that another live instance declares with another limit, period or burst is logged as a warning at start-up: instances that disagree pace a shared budget differently. During a deploy that changes a limit this passes by itself.
- Instances of SyncLane versions before the instances table do not register, so they are not seen by this check.

## Upgrading the schema

The schema is a numbered list of migrations. `synclane.migrations` records which ones a database has. With `CreateSchema` on, each instance applies what is missing at start-up, in one transaction, behind a lock; instances that start together wait for each other, and the second one finds nothing to do.

Migrations are only ever added, and each one only adds. An instance still running the previous version keeps working against the upgraded table, so you can roll a deploy one instance at a time.

A table created by SyncLane 0.1, before the migrations table existed, is adopted as it is: migration 1 finds the table and indexes in place and only records itself.

Migration 5 builds two indexes on `jobs`, and a plain `CREATE INDEX` blocks writes to the table while it runs. On a large table, create them first without blocking. The migration then finds them and skips them:

```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS jobs_open
    ON synclane.jobs (queue, owner, type) WHERE status <> 'completed';
CREATE INDEX CONCURRENTLY IF NOT EXISTS jobs_finished
    ON synclane.jobs (queue, completed_at) WHERE status = 'completed' AND mode <> 'initial';
```

## Running the schema yourself

Set `CreateSchema = false` and put the script into your own migrations. `SyncLaneSchema.Script()` returns it for any schema name; with EF Core that is one line:

```csharp
migrationBuilder.Sql(SyncLaneSchema.Script("synclane"));
```

When a SyncLane upgrade brings a new migration, add a new migration of your own with the same line. The script is safe to run on a database that already has some or all of it.

[schema.sql](schema.sql) is the same script for the default schema, checked against the library by a test.

With `CreateSchema` off SyncLane never changes the database. It checks at start-up that every migration it knows is recorded, and if one is missing it stops the host with a message that names the migration and the call that produces it.
