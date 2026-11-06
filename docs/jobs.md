# Working with jobs from your code

What the README's quick start leaves out: every call of `ISyncLane`, what a result means, how a handler says more than "it failed", who asked for a job, priorities, and rate budgets in a hand-written handler.

## Calls

`ISyncLane` is the only service you inject. Every call returns a result instead of throwing for problems you can expect:

| Call | Returns | What it is for |
| --- | --- | --- |
| `PlanAsync(queue, owner)` | `SyncResult<int>` | Plan one owner now, for example right after an account connects. |
| `EnqueueAsync(newJob)` | `SyncResult<long>` | Put one job in by hand, outside the plan. The value is the new job's id. |
| `RequeueAsync(queue, owner?)` | `SyncResult<int>` | Give failed, deferred and abandoned jobs fresh attempts. Cursors are kept. |
| `CancelAsync(queue, owner, reason)` | `SyncResult<int>` | Abandon what has not started, for example when an account disconnects. |
| `GetJobsAsync(query)` | `SyncListResult<Job>` | One page of jobs by queue, owner, status or type, newest first, or of the owners whose name or key contains `Search` ("acme" finds every store of "Acme Toys"). Follow `NextCursor` for the next page; set `IncludeTotal` for a count. |
| `GetProgressAsync(queue, owner)` | `SyncResult<OwnerProgress>` | How far the first load of an owner is. Use it to show "connecting" without storing a status of your own. |
| `MarkLoadedAsync(queue, ranges)` | `SyncResult<int>` | Moving from another system: record the history it already loaded, so only what is older and what came after is fetched. |
| `ForgetOwnerAsync(queue, owner)` | `SyncResult<int>` | Delete everything about an owner that is gone for good. If it comes back, its history loads again. |

```csharp
var result = await syncLane.EnqueueAsync(new NewJob { Queue = "shops", Owner = owner, Type = "orders", DedupKey = "resync-2026-10" });

if (!result.Succeeded)
    return result.Error!.Code == SyncErrorCodes.JobAlreadyExists
        ? Results.Ok("Already queued")
        : Results.Problem(result.Error.Message, statusCode: result.Error.Kind.ToHttpStatusCode());

return Results.Ok(result.Value);
```

## Who asked

`NewJob.RequestedBy` says who or what asked for a job, such as a user's id or `scheduler`: the handler reads it as `job.RequestedBy`, for example to send it along, and the monitor shows it. A sync-now records its caller there. It is the caller's data, never logged.

## Priority

Jobs that are due run in this order: `Urgent` jobs, then the owners an operator moved forward with `PrioritizeOwnerAsync`, then `High`, `Normal` and `Low`. Within a level, types run in the order you declared them. A job takes its level from `NewJob.Priority`, or else from its owner:

```csharp
// A stock change that must reach the marketplace before anything else in line.
await syncLane.EnqueueAsync(new NewJob { Queue = "shops", Owner = owner, Type = "inventory", Priority = JobPriority.Urgent });

// A new customer whose history should load before the others: give it from your owner source.
return accounts
    .Select(account => new JobOwner(account.Id, Name: account.Name) { Priority = account.IsNew ? JobPriority.High : JobPriority.Normal })
    .ToList();
```

An owner's priority applies to the jobs created after it changes; jobs already waiting keep theirs. Priority never cuts a running job: one account's jobs share a lane, so an urgent job of that account runs as soon as the job in its lane ends. `Job.PriorityLevel` and `Job.Prioritized` show where a job stands.

## Results and errors

A failed result always means the call was understood and the answer is no: an undeclared queue, a duplicate job, a page size out of range. It never hides an outage; a database that cannot be reached still throws. Every error has a stable code such as `SL3101`, listed with its cause and fix in [docs/errors.md](errors.md). Call `ThrowIfFailed()` or `GetValueOrThrow()` when you would rather have an exception.

Three exceptions let a handler say more than "it failed":

- `JobThrottledException` when the other side asks you to slow down, optionally with the wait it named.
- `JobAbandonedException` when a retry cannot help, for example when the account's access was revoked.
- `JobPostponedException` when the job only has to wait: it gives its slot back without using an attempt and continues from its cursor later. A rate budget throws it for you when a token is far away, and so does a recipe with a budget when the API says "slow down". A hand-written handler does the same after `ThrottledAsync`: `throw new JobPostponedException(reason, pause)`.

## Rate budgets in a handler

An API's limit belongs to an account, not to a job. Declare it once, and every job that calls the account draws from it:

```csharp
builder.Services.AddSyncLane(lane =>
{
    lane.AddBudget("example-shop", limit: 2, per: TimeSpan.FromSeconds(1), burst: 40);
    lane.AddQueue("shops", queue => { /* ... */ });
});
```

A recipe draws from it with `"rate": { "budget": "example-shop" }`. A hand-written handler calls `RateBudgets` before each request, and hands the permit back when the API answered `429`:

```csharp
var permit = await budgets.WaitAsync("example-shop", job.Owner, cancellationToken);
// ... send the request ...
if (response.StatusCode == HttpStatusCode.TooManyRequests)
    await budgets.ThrottledAsync(permit, response.Headers.RetryAfter?.Delta, cancellationToken);
```

- **One bucket per budget and key**, usually the owner. Owners that share one set of credentials pass one key.
- **Shared by every instance.** Buckets live in the `rate_buckets` table, so two instances together keep to the limit you declared, not twice it. Taking a token is one short statement, about 0.2 ms against a local database.
- **A `429` pauses the whole bucket** for `Retry-After`, or one token's time, at least a second. Every job on the key, on every instance, waits; then the bucket goes on at 0.7 times its rate and climbs back while requests go through. The pause doubles, up to five minutes, only while the first request after each pause is refused again. Requests that were already on their way when the pause began do not count again.
- **The limit is a ceiling, and the budget learns how much of it is safe.** A budget whose limit is right never sees a `429` and never changes. One whose limit is too high, or shared with another app, settles below it. For a limit nobody publishes, declare a generous ceiling and start lower: `AddBudget("example-shop", limit: 20, per: TimeSpan.FromSeconds(1), start: 0.25)`. Against simulated APIs with hidden limits it kept `429`s under 1% and reached 95 to 100% of the limit; it has not yet met a real one.
- **A wait longer than `MaxBudgetWait`** (30 seconds) gives the job's slot back without using an attempt, so a paused budget never holds workers.
- **What the API says counts too.** A recipe can name the headers in which the API says how many requests are left and when more come; the budget then stops at zero instead of running into a `429`, and notices another app spending the same quota. A hand-written handler passes the same numbers to `budgets.ReportRemainingAsync(permit, remaining, resetsIn)`. Those readings only ever lower the budget. See [docs/recipes.md](recipes.md#reading-what-the-api-says).
- **A limit the API reports replaces the declared one**, up or down, on every instance: name its header in the recipe (`rate.headers.limit`), or call `budgets.ReportLimitAsync(permit, limit, per)` from a handler. When the API raises its limit, SyncLane follows without a deploy.
- **An operator can lower a budget for a while**, without a deploy: `monitor.LimitBudgetAsync("example-shop", key: null, limit: 30, per: TimeSpan.FromMinutes(1), reason: "the API answers slowly", until: ...)`. It never raises one.
