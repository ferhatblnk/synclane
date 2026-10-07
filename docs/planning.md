# Planning

Planning decides which jobs exist. It is the part of a sync system that looks easiest and goes wrong most quietly, so it has one rule and this page explains where the rule comes from.

## The rule

> Planning compares the jobs that should exist with the jobs that exist, and adds only what is missing. It can be called at any time, any number of times.

Nothing in the planner remembers that it ran. There is no "already planned" flag, no early exit, no last-run timestamp. The table is the only memory.

## Two questions, two owners

Most planning bugs come from mixing these up:

| Question | Who answers it | What happens |
| --- | --- | --- |
| A job that should exist has **no row** | The planner | The row is inserted. |
| A row exists but the job **stopped** (failed, deferred, abandoned) | Retry and requeue | The row goes back to `pending`. |

This is why **coverage does not look at status**. A slice of history that was abandoned is still "planned". Bringing it back is the job of a requeue, which keeps its cursor. If the planner counted only healthy rows, it would insert a second row for the same slice while the first one is waiting for its retry, and the two would then fetch the same range.

## What should exist

You declare each job type as one of two kinds.

**Snapshot**: a type that reads everything on each run, like a product list.

- One `initial` job per owner.
- If `every` is set: one `recurring` job per period, as long as no job of the type is still outstanding for that owner.

**Windowed**: a type that reads a range of time, like orders.

- `initial` jobs that cover the range from the horizon to today in slices, newest first.
- If `every` is set: one `recurring` job per period whose window starts where coverage ends (minus `overlap`) and ends at the time of planning.

```
today    = midnight UTC of the current day
covered  = the oldest window_start among the owner's initial jobs, or today if there are none
horizon  = today - Horizon

for each slice from covered back to horizon:      # stops exactly at the horizon
    insert initial job (slice.start, slice.end)

if no recurring job of this type is outstanding and none was created in the current period:
    from = the newest window_end among all the owner's jobs of this type
    insert recurring job (from - Overlap, now)
```

The inserts go through `INSERT ... ON CONFLICT DO NOTHING` on the unique index `(queue, owner, type, dedup_key)`. The key is `initial` for a snapshot, `initial:<window start>` for a slice of history and `recurring:<period start>` for a recurring job. So even if two instances compute the same plan at the same moment, each job is created once.

## What that gives you

These are the numbers from `PlanningTests`, which runs the planner against PostgreSQL with a clock it controls:

| Situation | Jobs created |
| --- | --- |
| New owner, 60 days of history in 30-day slices | 2 |
| Planned again the same day | 0 |
| Planned again five hours later | 0 |
| Horizon raised from 60 to 730 days | 23, only the gap |
| Planned again after raising | 0 |
| Planned the next day | 0 |
| In total | 25 slices, 730 days, no hole and no overlap |

And for what is new:

- After an outage of any length, the next recurring job has **one** window that covers everything since coverage ended. Nothing has to be "caught up" by hand.
- While a recurring job is still pending or running, no second one is stacked behind it. The window of the next one simply starts where this one ends.
- A new job type added to a queue is planned for every owner that is already connected, on the next round.

## Three mistakes this design exists to prevent

All three were made in the system this design comes from. Each one looked reasonable when it was written.

### 1. The "already planned" latch

```csharp
if (await jobs.AnyAsync(job => job.Owner == owner && job.Mode == Initial))
    return;
```

One initial row blocked all further planning for that owner, forever. It was harmless until the history horizon was raised from 60 days to two years: new accounts got two years, and every account that was already connected silently stayed at 60 days. Nothing failed. Reports that needed last year's numbers were simply empty for the older accounts.

The planner has no early exit now. If you find yourself adding one, the generator underneath is probably not repeatable, which is the next mistake.

### 2. Slices anchored to the time of the call

```csharp
foreach (var slice in Slices(end: DateTime.UtcNow, days: 60))
```

Slices that start from "now" are different every time: called an hour later, every `window_start` has moved by an hour. Duplicates are recognised by `window_start`, so planning twice produced a second, overlapping set of slices. The latch above was there to hide exactly this.

The two were locked together: the latch existed because the generator was not repeatable. The order of the fix matters. **First anchor the slices to a fixed point, then remove the latch.** In SyncLane slices are anchored to midnight UTC, and the generator takes an explicit end point: it continues from the edge of what is covered and stops exactly at the horizon.

### 3. Deduplicating recurring jobs by their window

A recurring job also carries a window, so it is tempting to recognise a duplicate the same way as for history: same type, same `window_start`.

In that system the start of the recurring window was derived from the data: the latest "updated at" already stored, minus a few minutes of lag. Now consider a day on which a job brings nothing new. The latest "updated at" does not move. The next day the same window start is computed, it is found to be a duplicate, and nothing is planned. From that day on the account never gets a recurring job again, because the only thing that could move the data forward is the job that is no longer being planned. The lag made it worse: the last run had re-read a few boundary records, so its processed count was not zero and the account looked alive.

Six accounts stopped this way, one after the other, over several weeks, before anyone connected the dots.

So the two modes are deduplicated by different things, on purpose:

- `initial`: by window start. The set of slices is fixed and must not be planned twice.
- `recurring`: by period. "Has this owner had its job for this period?" has nothing to do with which range the job covers.

And the window of a recurring job is derived from the jobs table (where coverage ends), not from the data, so it moves forward even when a run finds nothing.

## Before you change something

- **A new job type**: declare it. Owners that are already connected get it on the next round.
- **A longer horizon**: change the number. The gap is filled, existing slices are untouched.
- **A different slice length**: existing slices stay as they are; only ranges that are not covered yet are cut with the new length.
- **Do not add an early return to the planner.** It is the one thing that breaks repeatability.
- **Do not filter coverage by status.** It collides with retry and requeue.
- **Do not deduplicate recurring jobs by window.** It can lock an owner out for good.
- **Do not delete initial rows** unless you want that history loaded again. They are the record of what has been planned. See [operations.md](operations.md#keeping-the-table-small).
