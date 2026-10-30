# Design: rate budgets

Status: parts 2.1 (budgets) and 2.2 (shared across instances) in review. Plan and evidence: [phase-2.md](phase-2.md).

## The problem

Today a lane does two jobs at once. It says "these jobs must not run at the same time", and the README also suggested it for "these accounts share a rate limit". The second use is too blunt: a lane runs one job at a time, so two accounts that share a limit of 1,000 requests a minute are pushed into single file, even when the limit would let both run at full speed.

And a `429` slows down only the job that got it. Every other job on the same account keeps sending requests into the same empty bucket, and collects its own `429`.

Real limits rarely match a lane or a job. They belong to an account, sometimes split by operation, and they move ([what large APIs do](phase-2.md#what-large-apis-do)).

## Decision

Keep the lane for exclusivity. Add a **rate budget** as a separate idea.

| | Lane | Rate budget |
| --- | --- | --- |
| Question it answers | May this job run now? | May this request go out now? |
| Granularity | One job | One request |
| Enforced by | Partial unique index on running jobs | Token bucket |
| Scope | `(queue, lane)` | `(budget name, key)` |

Jobs in different lanes, or different queues, that share a budget run side by side and draw from one bucket.

## Shape of the API

```csharp
builder.Services.AddSyncLane(lane =>
{
    lane.AddBudget("example-shop", limit: 2, per: TimeSpan.FromSeconds(1), burst: 40);
    lane.AddBudget("example-shop-reports", limit: 1, per: TimeSpan.FromMinutes(1));

    lane.AddQueue("shops", queue => { /* types as before */ });
});
```

- **Budgets are declared once, for the whole process**, not per queue: an account's limit is shared by every queue that calls it.
- **`limit` per `per`** is the steady rate. **`burst`** is how many may go at once after a quiet spell. It defaults to 1, which spaces requests evenly: the safe choice when the API's algorithm is unknown, and the only safe one for a fixed window until its reset header is read (part 2.3).
- **The key** says whose bucket it is. A recipe uses the job's owner unless it names another key, for owners that share one set of credentials.
- **`MaxBudgetWait`** (default 30 seconds) is the longest a request waits for a token inside a running job. A longer wait gives the job's slot back: the job is deferred until the token is due and the worker takes other work. A budget paused for an hour does not hold workers for an hour.

Recipes draw from a budget with one line ([recipes.md](../recipes.md#rate-budgets)):

```jsonc
"rate": { "budget": "example-shop" }
```

Hand-written handlers call the same service:

```csharp
public sealed class OrderHandler(RateBudgets budgets, ShopApi api) : IJobHandler
{
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        var permit = await budgets.WaitAsync("example-shop", job.Owner, cancellationToken);
        var page = await api.GetOrdersAsync(job, cancellationToken);
        if (page.TooManyRequests)
            await budgets.ThrottledAsync(permit, page.RetryAfter, cancellationToken);
        // ...
    }
}
```

A delegating handler that does this for an `HttpClient` comes with part 2.3.

## The bucket

Tokens refill continuously at `limit` per `per`, up to `burst`. A request **reserves** a token. If none is free, the count goes below zero and the request waits exactly until its token is due, so waiters queue in order instead of waking together and racing.

**A `429` pauses the budget for everyone on the key:**
- the bucket is emptied and stops refilling until the pause ends, then refills from zero, so requests restart at the steady rate rather than all at once;
- reservations made before the pause are void and are made again, behind it;
- the pause is `Retry-After` when the API sent one. Otherwise it is a backoff that starts at one token's time (at least a second, Shopify's own advice) and doubles while `429`s keep coming, up to five minutes. The streak is forgotten after a quiet spell twice as long as the last pause;
- only a `429` for a request sent after the latest pause began counts. Requests that left together come back together: in part 2.1 each of their `429`s doubled the backoff again, so eight of them without `Retry-After` paused an account for 128 seconds instead of one. `WaitAsync` now returns a permit that carries the bucket's pause count (`generation`), and `ThrottledAsync` takes it back. A late `429` only lengthens the pause when its `Retry-After` asks for longer.

The job that got the `429` is deferred until the pause ends, as before. Other jobs on the budget wait for it inside their page, or give their slot back when the wait is longer than `MaxBudgetWait`.

**Why not `System.Threading.RateLimiting`**, which the roadmap named:
- its token bucket cannot be paused or drained from outside;
- it does not run on `TimeProvider`, so the waits could not be tested without real sleeping;
- part 2.2 needs the bucket's state in a database row anyway.

The bucket is three SQL statements, tested with explicit times. Recorded as decision D18.

## Where the tokens live

**Part 2.1** kept them in memory: two instances together could go twice as fast.

**Part 2.2** keeps them in PostgreSQL, in `rate_buckets`: one row per budget and key, with the tokens left, when they were last refilled (in the future during a pause), the pause count (`generation`) and the backoff streak. The options considered:

1. **Split the limit by instance count.** Simple, but it needs to know how many instances run, and it wastes an idle instance's share. Rejected.
2. **One statement per token.** It locks the bucket's row, refills it up to now, and takes a token, or refuses one further away than `MaxBudgetWait`. Exact. **Chosen.**
3. **Reserve tokens in batches** and spend them in memory. Rejected after measuring option 2:
   - against a local PostgreSQL 18, a token costs about 0.16 ms (a bare `SELECT 1` is 0.03 ms), and one key serves about 12,000 tokens a second to eight callers;
   - the APIs in [phase-2.md](phase-2.md#what-large-apis-do) allow a few to a few dozen requests a second per account, and answer in 50 ms or more. Against a local database the statement adds about 0.3% to a 50 ms request; a database across a network adds its round trip, typically under a millisecond in one data centre;
   - tokens held by one instance cannot be used by another, and a batch spent late can exceed the burst: two instances each holding a burst's worth would send twice the burst at once.

The row of a bucket nobody has used is created full, in the same round trip as the statement that needs it. A request that waited for its token reads the row's pause count once more before it goes: another instance may have paused the bucket in the meantime, and then the token is void and taken again behind the pause.

**Clocks.** Refills are computed with the clock of the instance that asks, like leases. An instance whose clock runs a second ahead can let a second's worth of extra tokens through. Using the database's clock would avoid that, but every time in SyncLane comes from `TimeProvider`, so tests can move it; keeping instances on NTP is the documented requirement instead.

**What is lost.** A waiter whose host stops keeps its reserved token: it is not handed back. That errs on the slow side.

## Reading what the API says (part 2.3)

In order of trust:

1. **`Retry-After`**, seconds or an HTTP date. Standard. Read since part 1.2.
2. **`RateLimit` and `RateLimit-Policy`** from the IETF draft. Still an Internet-Draft, so it is read but not relied on.
3. **Headers a recipe declares:** the remaining count and the reset, with their format.
   - ShipStation v1 sends both on every response, which makes a fixed window exact.
   - Walmart sends a token count.
   - Amazon sends its rate, which may lower a declared limit but never raise it.

   They are configured in the recipe, never named in the library.

## Learning a limit (part 2.4)

When `learn` is on, or a budget has no limit:

- Start at a conservative rate (a setting, default one request a second).
- On `429`: halve the rate and pause as above.
- After a full window without a throttle: raise the rate by 10%.
- Never go above a declared limit. A declared limit is a ceiling; learning only finds out how much of it is safe.

## What it will not do

- **Probe for the limit by going fast on purpose.** It is someone's seller account. Learning only ever starts low and climbs.
- **Guarantee zero `429`.** Other apps on the same account draw from the same limit on the provider's side, and some APIs share pools across all their partners. SyncLane cannot see them; it can react well.
- **Put the owner in a metric.** Budget metrics carry the budget's name, never its key. A `BudgetPermit` prints the budget only.

## Open questions

- Cost-based limits, such as GraphQL points: a request taking more than one token. Waits for recipes that speak GraphQL.
- A throttle reported in a 200 body: same.
- Should a job type be able to declare its budget, so the planner does not start jobs whose budget is paused? Possible later; for now the slot is given back quickly.
