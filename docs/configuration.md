# Configuration

Every setting has a default that is right for most installations; set one only for the case its description names.

| Setting | Default | What it does |
| --- | --- | --- |
| `ConnectionString` | required | The database that holds the table. |
| `ListenConnectionString` | `ConnectionString` | The same database, for the one connection per process that hears job changes. Set it when `ConnectionString` goes through a pooler in transaction mode. See [Behind a connection pooler](operations.md#behind-a-connection-pooler). |
| `ListenForChanges` | `true` | Turn it off where no connection can listen: workers then look for jobs every `PollInterval`, and nothing is opened or sent to hear changes. |
| `Schema` | `synclane` | The schema of the `jobs` table. |
| `CreateSchema` | `true` | Create the schema and apply missing migrations at start-up. Turn it off if you manage migrations: put `SyncLaneSchema.Script()` into one of yours, and SyncLane only checks at start-up that it has been applied. See [docs/operations.md](operations.md#running-the-schema-yourself). |
| `RunPlanner`, `RunWorkers` | `true` | Turn them off in instances that should only enqueue and read. |
| `PlanInterval` | 1 minute | How often the planner looks for missing jobs. |
| `PollInterval` | 5 seconds | How long an idle worker slot waits before it looks again. A job created or put back in line wakes it at once, from any instance. |
| `AddBudget(name, limit, per, burst, start)` | none | A rate budget: at most `limit` requests per `per`, at most `burst` at once (default 1, evenly spaced). It starts at `start` times the limit (default 1) and learns below it. |
| `Application` | the host's application name | The name instances register under. Copies of one application share its queues; another application that runs one of them is refused at start-up. See [Several applications on one schema](operations.md#several-applications-on-one-schema). |
| `KeepCompletedJobs` | 7 days | How long completed recurring and manual jobs stay before instances that run the planner delete them. Initial jobs and unfinished jobs stay. `null` keeps everything. |
| `MaxBudgetWait` | 30 seconds | The longest a request waits for a budget's token inside a job; a longer wait postpones the job. |
| `queue.Concurrency` | `1` | Jobs of the queue one process runs at once. `1` makes the queue strictly sequential. |
| `queue.Ordered` | `false` | Take each lane's jobs strictly in the order they were created: one that failed or was abandoned holds the jobs behind it until it completes or is skipped. Only that queue and lane wait. See [Ordered queues](operations.md#ordered-queues). |
| `queue.MaxAttempts` | `5` | Attempts in a row without saving a page before a job is deferred. |
| `queue.LeaseDuration` | 5 minutes | How long a silent worker keeps its job. |
| `queue.RetryBaseDelay`, `RetryMaxDelay` | 1 minute, 1 hour | Delay after a failure: base, doubled per attempt, capped. |
| `queue.ThrottleBaseDelay`, `ThrottleMaxDelay` | 5 minutes, 30 minutes | The same for a `JobThrottledException` that names no wait. |

## Settings from configuration

The code declares queues, job types, handlers and budgets. Their numbers can then come from configuration, per environment, without a deploy:

```csharp
builder.Services.AddSyncLane(builder.Configuration.GetSection("SyncLane"), lane =>
{
    lane.ConnectionString = builder.Configuration.GetConnectionString("Postgres")!;
    lane.AddBudget("shop-api", limit: 2, per: TimeSpan.FromSeconds(1), burst: 40);
    lane.AddQueue("shops", queue => queue
        .Owners<ConnectedShops>()
        .Windowed<OrderHandler>("orders", horizon: TimeSpan.FromDays(730), slice: TimeSpan.FromDays(30), every: TimeSpan.FromMinutes(15)));
});
```

```json
"SyncLane": {
  "RunWorkers": false,
  "Queues": {
    "shops": {
      "Concurrency": 4,
      "Types": { "orders": { "Every": "00:05:00", "Slice": "7.00:00:00" } }
    }
  },
  "Budgets": { "shop-api": { "Limit": 4 } }
}
```

- Everything in the table above can be set: top-level settings by name, `Queues:<queue>` for a queue, `Queues:<queue>:Types:<type>` for `Horizon`, `Slice`, `Every`, `Overlap` and `Offset`, and `Budgets:<budget>` for `Limit`, `Per`, `Burst` and `Start`.
- Configuration overrides what the code declares and never adds to it. A queue, type, budget or setting the code does not know stops the start with `SL1012`, naming its path, so a typo never passes silently. Every problem is reported at once.
- Names ignore case, as configuration keys do, so an environment variable such as `SYNCLANE__QUEUES__SHOPS__CONCURRENCY=4` works.
- Time spans are written `hh:mm:ss` or `d.hh:mm:ss`. A value that cannot be read stops the start with `SL1013`, which names the path but never the value.
- The values are checked like values from code.
