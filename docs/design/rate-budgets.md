# Design: rate budgets

Status: part 2.1 in review: budgets in one process. Plan and evidence: [phase-2.md](phase-2.md).

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
        await budgets.WaitAsync("example-shop", job.Owner, cancellationToken);
        var page = await api.GetOrdersAsync(job, cancellationToken);
        if (page.TooManyRequests)
            budgets.Throttled("example-shop", job.Owner, page.RetryAfter);
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
- the pause is `Retry-After` when the API sent one. Otherwise it is a backoff that starts at one token's time (at least a second, Shopify's own advice) and doubles while `429`s keep coming, up to five minutes. The streak is forgotten after a quiet spell twice as long as the last pause.

The job that got the `429` is deferred until the pause ends, as before. Other jobs on the budget wait for it inside their page, or give their slot back when the wait is longer than `MaxBudgetWait`.

**Why not `System.Threading.RateLimiting`**, which the roadmap named:
- its token bucket cannot be paused or drained from outside;
- it does not run on `TimeProvider`, so the waits could not be tested without real sleeping;
- part 2.2 needs the bucket's state in a database row anyway.

The bucket is under a hundred lines, with tests on a manual clock. Recorded as decision D18.

## Where the tokens live

**Part 2.1:** in memory, one bucket per budget and key. Each instance has its own buckets, so two instances together can go twice as fast. With one instance, or with a budget split by hand (half the limit each), it is exact.

**Part 2.2:** shared through PostgreSQL. The options considered:
1. **Split the limit by instance count.** Simple, but it needs to know how many instances run, and it wastes an idle instance's share. Rejected.
2. **One database round trip per request.** An atomic `UPDATE … RETURNING` on a `rate_buckets` row. Exact, but it adds a round trip to every call: at 100 a minute that is nothing, at 50 a second per key it is too much.
3. **Reserve tokens in small batches.** An instance takes up to `n` tokens from the row in one statement and spends them in memory. Unused tokens expire with the window. **Chosen**, with `n` adaptive: 1 when the limit is low, more when it is high. A pause is written to the row, so every instance sees it.

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
- **Put the owner in a metric.** Budget metrics carry the budget's name, never its key.

## Open questions

- Cost-based limits, such as GraphQL points: a request taking more than one token. Waits for recipes that speak GraphQL.
- A throttle reported in a 200 body: same.
- Should a job type be able to declare its budget, so the planner does not start jobs whose budget is paused? Possible later; for now the slot is given back quickly.
