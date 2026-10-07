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

When an account is removed for good, delete all its rows. When it only disconnects, call `CancelAsync` and stop returning it from your `IOwnerSource`. If it connects again, call `RequeueAsync` for it: its abandoned slices are still planned and come back with their cursors.

## Choosing the numbers

**`slice`** is the setting with the most consequences. One slice is one job, and a job is the largest unit of work that cannot be split. Work out how long one slice takes for your biggest account at the other side's rate limit.

The system this design comes from once tripled its slice from 30 to 90 days to "have fewer jobs". For the largest account a single slice then held about 43,000 records, 430 pages, roughly twenty hours of requests, against page tokens that lived for twenty-four. Every hiccup late in a slice cost most of a day. Small slices finish, and a finished slice is progress that cannot be lost.

**`LeaseDuration`** only needs to cover the time it takes to notice a dead process, because the heartbeat renews it during long pages. The default of five minutes means a job whose worker died waits at most five minutes to be taken over. Going much lower mostly buys false alarms during garbage collection pauses or a slow database.

**`Concurrency`** is per process and per queue. Three instances with `Concurrency = 4` run up to twelve jobs of that queue at once, and still at most one per lane. Set it to `1` for an API that must be called strictly one request at a time, whatever the account.

**`MaxAttempts`** counts every pick-up, including ones that ended in a throttle. With the defaults a job that keeps failing is deferred after about fifteen minutes of ordinary failures, or more than an hour of throttling.

**`every` and `overlap`**: a recurring window starts `overlap` before the previous one ended. Use the overlap to cover records that show up late on the other side, and rely on upserts to make the double read harmless.

## Several instances

Run as many instances as you like against the same table. Planning is safe to do from all of them, and so is taking jobs.

If your web instances should not do background work, set `RunWorkers = false` and `RunPlanner = false` there. `ISyncLane` works the same either way.

Keep the clocks of your instances in sync. Leases and due times are compared with the clock of the instance that asks.

## Running the schema yourself

Set `CreateSchema = false` and apply [schema.sql](schema.sql) through your own migrations. The file is checked against the library by a test, so it is always the statement the library would have run. For a schema other than `synclane`, replace the name in the file and set `Schema` to match.
