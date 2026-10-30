# Error codes

Every error SyncLane reports has a stable code. A code is never reused or renumbered, so it is safe to branch on, to alert on and to use as a localization key. The message that comes with it is English and may be reworded; the code does not change.

The first digit is the area:

| Range | Area | How it reaches you |
| --- | --- | --- |
| `SL1xxx` | Configuration | `SyncLaneException` from `AddSyncLane`, before the application starts. |
| `SL2xxx` | Schema and stored data | `SyncLaneException` at start-up, or when a row cannot be read. |
| `SL3xxx` | Calls to `ISyncLane` | A failed `SyncResult`, never an exception. |
| `SL4xxx` | Running jobs | Text in the job's `error` column, starting with the code. |

`SyncErrorCodes` holds every code as a constant. A test checks that each one is listed on this page.

## Configuration

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL1001` | Configuration | No connection string was set. | Set `options.ConnectionString`, for example from configuration. |
| `SL1002` | Configuration | The schema name is not a plain lower-case identifier. It is placed into SQL as an identifier, so anything else is refused. | Use lower-case letters, digits and underscores, starting with a letter or underscore. |
| `SL1003` | Configuration | `PlanInterval` or `PollInterval` is zero or negative. | Use a positive interval. |
| `SL1004` | Configuration | Two queues have the same name. | Give each `AddQueue` call its own name. |
| `SL1005` | Configuration | Two job types of one queue have the same name. | Give each type of a queue its own name. |
| `SL1006` | Configuration | A queue setting cannot work, such as `Concurrency = 0` or a `RetryMaxDelay` below `RetryBaseDelay`. The message names the setting and the rule. | Change the setting as the message says. |
| `SL1007` | Configuration | A job type setting cannot work, such as a zero `slice` or an `offset` longer than `every`. | Change the setting as the message says. |

## Schema and stored data

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL2001` | Configuration | `CreateSchema` is off and the database lacks a migration this version needs. | Run `SyncLaneSchema.Script()` in your own migration, or turn `CreateSchema` on. |
| `SL2002` | Internal | A row holds a status this version does not know. It was written by a newer version or by hand. | Run the same version on every instance, or correct the row. |
| `SL2003` | Internal | A row holds a mode this version does not know. | The same as `SL2002`. |

## Calls to ISyncLane

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL3001` | NotFound | The call names a queue that was not declared. | Declare the queue in `AddSyncLane`, or check the name. |
| `SL3002` | NotFound | `EnqueueAsync` names a job type that is not declared on the queue. | Declare the type with `Snapshot`, `Windowed` or `Handle`. |
| `SL3003` | Validation | `JobQuery.Limit` is below 1 or above `JobQuery.MaxLimit`. | Ask for a smaller page and follow `NextCursor`. |
| `SL3004` | Validation | `JobQuery.Cursor` was not produced by SyncLane. | Pass the previous page's `NextCursor` unchanged. |
| `SL3101` | Conflict | `EnqueueAsync` found a job with the same owner, type and dedup key. | Requeue the existing job, or use another key. |

## Running jobs

These are written to the `error` column of the job, which ends `abandoned`.

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL4001` | Configuration | A worker took a job whose type has no handler in that process, usually because instances run different versions or configurations. | Declare the type on every instance that runs workers, then requeue. |
| `SL4002` | Internal | The job was taken more often than its attempts allow without ever reporting a result. Its worker most likely dies while running it. | Look for crashes or out-of-memory kills during this job, fix them, then requeue. |

## Errors that are not on this page

- **Exceptions your handler throws** are stored in the `error` column as they are, type and message, and drive the retry policy. They have no SyncLane code.
- **A `null` argument** throws `ArgumentNullException`. It is a bug in the calling code, not a condition to handle.
- **A database that cannot be reached** throws the `NpgsqlException` that Npgsql raised. Inside a worker it ends the attempt and the job is retried.
- **Cancellation** throws `OperationCanceledException`. It is never reported as a failure.
