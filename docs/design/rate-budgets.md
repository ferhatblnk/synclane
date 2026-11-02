# Design: rate budgets

Status: parts 2.1 and 2.2 merged; 2.3 (reading what the API says) in review; 2.4 (learning a limit) in review. Plan and evidence: [phase-2.md](phase-2.md).

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
- the pause is `Retry-After` when the API sent one. Otherwise it is one token's time (at least a second, Shopify's own advice). Since part 2.4 it doubles, up to five minutes, only while the first request after each pause is refused again; in 2.1 it doubled with every `429` until a quiet spell, which part 2.4 found harmful;
- only a `429` for a request sent after the latest pause began counts. Requests that left together come back together: in part 2.1 each of their `429`s doubled the backoff again, so eight of them without `Retry-After` paused an account for 128 seconds instead of one. `WaitAsync` now returns a permit that carries the bucket's pause count (`generation`), and `ThrottledAsync` takes it back. A late `429` only lengthens the pause when its `Retry-After` asks for longer.

The job that got the `429` waits until the pause ends. Since part 2.5 it gives its slot back without using an attempt, because the budget's pause is what protects the API. Other jobs on the budget wait for it inside their page, or give their slot back when the wait is longer than `MaxBudgetWait`.

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

A declared budget is a guess made in advance. Many APIs say on every answer how much is left. Reading that lets SyncLane stop at zero instead of running into a `429`, notice when another app on the account uses the same quota, and notice when a limit was lowered.

### What answers say

Checked against the official pages on 2026-10-30.

| Pattern | Example | What it means | Declared in a recipe as |
| --- | --- | --- | --- |
| Remaining, and seconds to the reset | ShipStation v1: `X-Rate-Limit-Remaining: 12`, `X-Rate-Limit-Reset: 41` | 12 left in this window; a new window in 41 seconds | `remaining`, `reset` with `resetFormat: seconds` |
| Remaining, and the reset as a time | GitHub: `x-ratelimit-remaining: 0`, `x-ratelimit-reset: 1793361600` | Nothing left until that Unix time. When the limit is exceeded GitHub answers `403` or `429` | `resetFormat: unix` |
| A token count, no reset | Walmart: `x-current-token-count` | Tokens left in a bucket that refills | `remaining` only |
| Used of a size, no reset | Shopify REST: `X-Shopify-Shop-Api-Call-Limit: 32/40` | 32 of a bucket of 40 used; it leaks at a known rate | `remainingFormat: used/limit` |
| IETF draft 11 | `RateLimit: "default";r=50;t=30` | At most 50 within the next 30 seconds. The draft says a client "MUST NOT assume that all its service limit will be fully restored" after that | `remaining: RateLimit`, `remainingFormat: ietf` |
| A rate only | Amazon: `x-amzn-RateLimit-Limit: 0.5` | The operation's rate. "May not be returned" | Part 2.4: it can lower a learned rate |
| In the body | Shopify GraphQL: `extensions.cost.throttleStatus` | Cost points left and their restore rate | Later, with GraphQL recipes |

No header is read unless the recipe declares it, the IETF one included: its draft has changed shape twice, and an API that sends it may count a different partition than the budget's key. Platform names stay in recipes and in this page, never in the library.

### Rules

1. **A reading only ever lowers.** The bucket drops to what the API says is left; it never gains tokens beyond its declared rate and burst. A declared budget stays the ceiling.
2. **Requests still on their way are counted.** The API's count is as of the request it answers. Requests that took a token after that one have not been counted yet, and may be in flight on any instance. Every token carries a sequence number from the bucket's row (`taken`, migration 3), so `left = remaining - tokens taken since`. Without it, eight requests in flight against a window with three left would all go, and five would get `429`. The error is on the safe side: a request that overtook the one answered is subtracted twice, so SyncLane may stop a request or two early.
3. **Nothing left and a reset known: pause until the reset.** Every instance waits; the bucket restarts empty, as after a `429`, because neither a reset header nor the IETF window promises the whole quota back. This is not a throttle: it does not lengthen the backoff of later `429`s and is counted in its own metric, `synclane.budget.exhausted`.
4. **Nothing left, no reset known:** the bucket goes below zero and refills at its declared rate.
5. **A reading that cannot be parsed is ignored**, as the IETF draft requires. Values are numbers or times; none is ever logged.
6. **A time is measured against the answer's `Date` header** when it has one (Unix times and HTTP dates), so a skew between the API's clock and the instance's does not move the pause.
7. **A `429` without `Retry-After` waits for the reset**, at least one second. `Retry-After` wins when both are present; the IETF draft says it MUST.
8. **A `403` is a throttle when it carries `Retry-After`, or when its declared remaining is zero.** GitHub answers a spent limit with `403` and `x-ratelimit-remaining: 0`; until now such a job was abandoned as unauthorised. A `403` without either signal still abandons.

### What it cannot do

- **Promise zero `429` when another app spends the same quota continuously.** With a reset known, SyncLane stops at zero exactly. Without one, it corrects itself on every answer, but the other app can spend the last token between two answers.
- **Fix a declared rate that is too high.** A count without a reset corrects the bucket's level, not its rate. Measured against a hidden bucket of 6 a second with a budget of 8: `429`s fell from 12% to 7 to 10% of requests, and did not stop. Lowering the rate is part 2.4.
- **Fix a budget declared above a window's quota with a burst larger than it.** After a pause the bucket refills to its declared burst; declare a burst no larger than the window's quota.
- **Know how an API rounds.** A reset in whole seconds may be rounded down. A request may then arrive just before the window turns; its `429` waits for the next reset.

Hand-written handlers report the same readings with `RateBudgets.ReportRemainingAsync(permit, remaining, resetsIn)`. A delegating handler that does this for an `HttpClient` is left for a later part: it needs the current job's owner without being told, which is a new engine API of its own.

## Learning a limit (part 2.4)

A declared limit is often wrong: undocumented (TikTok Shop), different per seller (Amazon's dynamic usage plans), lowered for a while (Shopify), or shared with another app. Part 2.3 showed the cost: a budget 33% above the real rate kept 7 to 10% of its requests throttled, even with the API's count in hand, because a count fixes the bucket's level and not its rate.

### Prior art

- **TCP congestion control.** Additive increase, multiplicative decrease (AIMD): cut the rate on a loss, grow it slowly otherwise. CUBIC returns quickly to the rate of the last loss and probes slowly beyond it. RFC 7661: do not grow while the sender is not using what it has.
- **The AWS SDKs' adaptive retry mode**, the closest thing to this problem (botocore, Apache 2.0, read on 2026-10-31): a client-side token bucket, cut to 0.7 times the measured rate on a throttle, CUBIC recovery with a constant of 0.4, the rate held to at most twice what is measured. It starts unlimited and only engages after the first throttle. Two things do not carry over:
  - its CUBIC constant is in absolute requests a second, tuned for APIs that take thousands a second; at half a request a second it would multiply the rate sevenfold within three seconds;
  - it starts high, which this project rules out unless the user declared the number.

### Measured before deciding

A discrete-event simulation (kept outside the repository; the benchmark that guards the result is in the tests) ran each candidate for 30 simulated minutes against servers with a hidden limit:
- token buckets of 10, 2 and 0.5 a second, with bursts from 5 to 40, the last like Shopify REST's bucket of 40 leaking 2 a second;
- a fixed window of 40 a minute, with and without `Retry-After`;
- another app spending 40% of a bucket (this server was wrong: the other app's requests piled up during a pause and landed at once; the gate's corrected server gives 94.7% throughput and 0.58% `429`);
- a limit that halves halfway;
- one operation of one request a minute.

It used two loads (4 jobs at 150 ms latency, 8 jobs at 500 ms). The bucket and pause rules were the ones in the library.

| Candidate | Result |
| --- | --- |
| Fixed at half the hidden limit (the cautious choice) | 50% throughput, no `429` |
| Time-based AIMD, +1% a second | 85 to 100% throughput, but 4 to 5% `429` at low rates: a cycle lasts the same time whatever the rate, so it holds fewer requests when the rate is low |
| CUBIC made scale-free | Collapsed to 30% throughput on the large bucket and 27% `429` at 0.5 a second |
| **Per-token AIMD with three zones** (chosen) | 91 to 100% throughput, 0.34 to 0.94% `429`, in every scenario but the two below |

Two findings changed the design beyond the controller:
- **The 2.1 backoff rule was a trap.** A pause doubled with every `429` until a quiet spell, even when requests succeeded in between. A rate being lowered step by step hits a `429` after every step, so pauses climbed to five minutes: a budget twice too high with another app on the account fell to 0.8% throughput. A pause should lengthen only when the first request after the previous pause is refused again: then the API is still full. A `429` after some requests went through is a new overshoot, and lowering the rate deals with it. With learning, this alone took a fixed window without headers from 72% to 91–96% throughput.
- **Starting at a declared ceiling four times too high did nearly as well as starting low:** a cut is multiplicative, so four `429`s bring it down. So every budget adapts. Its declared limit is both its start and its ceiling, and a budget whose limit is right never sees a `429` and never changes.

Where it does not reach the gate, honestly:
- **One request a minute.** In 30 minutes only 20 requests go out. Nothing can be learned from 20 requests: starting below the limit reached 67–70% of it, and starting above it cost 6 or 7 `429`s (11 to 13%). For slow operations, declare the documented rate.
- **A fixed window that sends `Retry-After`,** at 8 jobs: 1.02%. A window refuses at its end whatever the rate was; the wait is exact, so each such `429` costs little.

### The rule

Every budget keeps a current rate in its row, shared by every instance. It starts at `start` times the declared limit (default 1) and never goes above the limit.

- **A fresh `429`** (one for a request sent after the last pause began) cuts the rate to 0.7 times what it was, but not below 1% of the limit. The rate before the cut is remembered as the last bad rate.
- **Every token taken while the budget is the bottleneck** raises the rate: the bucket was empty and not paused, so demand met the rate. The rise per token depends on where the rate is:
  - 2% below 90% of the last bad rate: return quickly to where it was fine;
  - 0.03% between 90% and 100% of it: linger near the edge;
  - 0.1% above it: look for more, slowly, in case the limit was raised.

  Before the first `429` the last bad rate is the declared limit, so a budget started low climbs quickly to 90% of it.
- **Growth is per token, not per second,** so a cycle holds about the same number of requests at any rate, and so does its share of `429`s. **No growth while idle** (RFC 7661): an hour without requests does not leave the rate high for the next burst.
- **The pause after a fresh `429`** is `Retry-After`, or one token's time at the rate before the cut, at least a second. It doubles only while the first request after each pause is refused again, up to five minutes.
- **What 2.3's headers say is unchanged:** a window that runs out is quota, not rate, and does not cut the rate.

`start` below 1 is for limits nobody publishes: the rate begins at that share of the ceiling and climbs. A ceiling must always be declared. SyncLane never goes above what the user wrote.

## What it will not do

- **Probe for the limit by going fast on purpose.** It is someone's seller account. Learning only ever starts low and climbs.
- **Guarantee zero `429`.** Other apps on the same account draw from the same limit on the provider's side, and some APIs share pools across all their partners. SyncLane cannot see them; it can react well.
- **Put the owner in a metric.** Budget metrics carry the budget's name, never its key. A `BudgetPermit` prints the budget only.

## Open questions

- Cost-based limits, such as GraphQL points: a request taking more than one token. Waits for recipes that speak GraphQL.
- A throttle reported in a 200 body: same.
- Should a job type be able to declare its budget, so the planner does not start jobs whose budget is paused? Possible later; for now the slot is given back quickly.
