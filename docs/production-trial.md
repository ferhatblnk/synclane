# Production trial

Before you rely on SyncLane, run one real integration with it for 30 days. This page is the checklist for that trial: what to set up, what to break on purpose, what to watch and when to stop.

## Before the first run

- [ ] **Pick one integration that is allowed to fail.** One provider, a handful of accounts, data you can reload. Not the integration your business depends on today.
- [ ] **Keep the old path running beside it** for the first two weeks, writing to a different table, so you can compare record counts.
- [ ] **Start slow.** `Concurrency = 1` per queue, a `slice` of 7 days, a `horizon` of 30 days. Raise them after the first week if nothing complains.
- [ ] **Make every write an upsert.** A page can be handled twice (see "What it promises" in the README).
- [ ] **Throw `JobThrottledException` on `429`**, with the provider's `Retry-After` when it sends one, and `JobAbandonedException` when access was revoked.
- [ ] **Wire metrics and traces** (see [operations.md](operations.md#metrics-and-traces)) and set the three alerts listed there.
- [ ] **Decide who owns the schema.** Either `CreateSchema = true`, or `SyncLaneSchema.Script()` in your own migration.
- [ ] **Read [SECURITY.md](../SECURITY.md)**, especially the part about what ends up in the `error` column.

## Drills, in the first week

Run each once and write down what happened.

| Drill | How | Expected |
| --- | --- | --- |
| Graceful stop | Stop the app while a job is running. | The job is `pending` again with its cursor; the log says it was handed back. |
| Crash | `kill -9` the process while a job is running. | After `LeaseDuration` another instance (or the restarted one) takes the job and continues from the cursor. `lease_lost` stays at zero. |
| Rolling deploy | Deploy a new build one instance at a time while jobs run. | No job runs twice at once; no migration error. |
| Two instances | Run two instances against the same table for a day. | Every job is created once; never two running jobs in one lane. |
| Provider outage | Point one account at a wrong host for an hour. | Its jobs fail, back off, defer and finally abandon. Other accounts are not slowed down. Requeue brings it back from the cursor. |
| Revoked access | Revoke one account's token. | Its jobs end `abandoned` after one attempt if the handler throws `JobAbandonedException`. |

## Every day

One query and one look at the dashboard:

```sql
SELECT status, count(*)
FROM synclane.jobs
WHERE updated_at > now() - interval '1 day'
GROUP BY status;
```

- Anything `abandoned`: read the `error`, fix the cause, requeue.
- `synclane.job.throttled` rising: lower `Concurrency` or widen `every`.
- `synclane.job.wait` rising: more worker slots, or a lane that never gets idle.

## Keep a log

One line per incident in a file next to the integration: date, what happened, which job ids, what fixed it, and whether it was SyncLane's fault, the handler's or the provider's. The log is what tells you, at the end, whether SyncLane is ready for the next integration.

## Exit: the trial is done when

- 30 days have passed with no data lost and no job run twice at the same time in one lane;
- every drill above has been run and behaved as expected;
- record counts match the old path for the two weeks they ran side by side;
- every incident in the log has a cause.

## Stop: pause new features when

- a job's cursor was lost or went backwards;
- two jobs of one lane ran at the same time;
- a migration failed on start-up.

Any of these is a bug in the core. Fix it, add a test that reproduces it, and restart the 30 days.
