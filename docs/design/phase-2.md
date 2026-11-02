# Plan: phase 2, rate budgets

Status: in progress. Each part is one pull request. A part is merged only when its gate passes. The design is in [rate-budgets.md](rate-budgets.md); this page is the plan and the evidence behind it.

## Goal

Requests to one account go out at a pace the account allows, however many jobs, queues and instances share it. When the other side says "slow down", everything that shares the account slows down, not only the job that heard it.

**Phase gate (from the roadmap):** against a fake server with a limit SyncLane is not told, fewer than 1% of requests get `429`, and throughput reaches at least 80% of the hidden limit. **Stop rule:** if learning a limit does worse than a fixed, cautious limit, learning becomes opt-in and the default is a fixed limit.

## What large APIs do

Read on 2026-10-30. Official pages first; anything else is marked.

| API | Algorithm | Kept per | What a response tells | On `429` | Source |
| --- | --- | --- | --- | --- | --- |
| Amazon Selling Partner API | Token bucket, rate and burst | Operation, and selling partner with application | `x-amzn-RateLimit-Limit`: the rate only, and "may not be returned" | No `Retry-After` documented; "requires a back-off strategy" | Official ([usage plans and rate limits](https://developer-docs.amazon/sp-api/docs/usage-plans-and-rate-limits)) |
| Walmart Marketplace | Token bucket | API, and seller; each solution provider app has its own | `x-current-token-count`; next refill time (spelt `X-Next-Replenishment-Time` in one table, `x-next-replenish-time` elsewhere; format not documented) | `429` | Official ([rate limiting](https://developer.walmart.com/us-marketplace/docs/rate-limiting)) |
| ShipStation API v1 | Fixed window, 40 a minute | API key and secret | `X-Rate-Limit-Limit`, `-Remaining`, `-Reset` (seconds to the next window) on every response | `429`; wait `X-Rate-Limit-Reset` seconds | Official ([v1 requirements](https://www.shipstation.com/docs/api/requirements/)) |
| ShipStation API v2 | 200 a minute by default | Not stated | Nothing documented | `429` with `Retry-After` seconds; "implement retry logic at a global level" | Official ([rate limits](https://docs.shipstation.com/rate-limits)) |
| Shopify REST Admin (legacy since 2024-10) | Leaky bucket: 40 with 2 a second leaking (Plus: 400 and 20) | App and store | `X-Shopify-Shop-Api-Call-Limit: 32/40` (used of size) | `429` with `Retry-After: 2.0` | Official ([REST rate limits](https://shopify.dev/docs/api/admin-rest/usage/rate-limits)) |
| Shopify GraphQL Admin | Cost points: 100 a second on the standard plan up to 2,000; one query at most 1,000 | App and store | `extensions.cost.throttleStatus` in the body: available, maximum, restore rate | Error `THROTTLED`; GraphQL can answer 200 for errors | Official ([GraphQL rate limits](https://shopify.dev/docs/apps/build/apis/graphql-admin/rate-limits)) |
| TikTok Shop | **Not publicly documented.** Third parties report a per-second limit that changes with the number of authorised shops, no way to ask for it, and `429` even under one's own limit because some endpoints share a pool | Reported: app and shop | Nothing reported | `429` | Third party only; the reports disagree on numbers ([Phyllo](https://www.getphyllo.com/post/tiktok-api-rate-limits-in-2026-quotas-errors-workarounds), [OneCart](https://www.getonecart.com/help/why-tiktok-shop-net-amount-data-is-currently-unavailable/)) |

Also: the IETF `RateLimit` and `RateLimit-Policy` headers are still an Internet-Draft (version 11, May 2026), not an RFC ([datatracker](https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/)).

## What we take from it

1. **One model covers them: a token bucket with a rate and a burst.**
   - A leaky bucket is the same thing seen from the other side.
   - A fixed window is covered safely by a small burst, and exactly once its reset header is read (part 2.3).
   - A cost-based limit is a bucket where a request takes more than one token (later).
2. **A limit belongs to an account, not to a job or a lane.** Amazon, Walmart and Shopify keep it per seller or store and per app; ShipStation per key.
   - So a budget is kept per key: by default the job's owner, or an explicit key when several owners share credentials.
   - Some APIs split it further by operation; a recipe names the budget it draws from, so one account can have several budgets.
3. **A `429` must slow down everyone on the account.** ShipStation says so outright.
   - Today one job is throttled while the others keep firing into the same empty bucket.
   - From part 2.1, a `429` pauses the whole budget, and requests restart slowly after the pause.
4. **`Retry-After` is the exception, not the rule.** Amazon and Walmart do not send it; ShipStation v1 uses its own reset header. So:
   - a pause without `Retry-After` needs its own backoff, doubling while `429`s continue;
   - reading provider headers must be configurable per recipe, because no two are alike, and no platform may be named in the library.
5. **Limits move.**
   - Amazon has dynamic plans.
   - Shopify "may temporarily reduce API rate limits".
   - TikTok Shop publishes none.

   A declared limit is a ceiling, never a promise; learning (part 2.4) only ever starts low and climbs.
6. **A `429` is not always our fault.** Other apps draw from the same account, and some APIs share pools across all partners. Zero `429` cannot be promised; reacting well to them can.
7. **A throttle can arrive as HTTP 200** (Shopify GraphQL). Recipes do not speak GraphQL yet; noted for when they do.

## Parts

### 2.1 Budgets in one process (merged)

- `AddBudget(name, limit, per, burst)` on the engine's options, shared by every queue.
- `RateBudgets.WaitAsync(budget, key)`: a token bucket of our own, on `TimeProvider`.
  - A request that would wait longer than `MaxBudgetWait` gives its job's slot back instead (`JobPostponedException`), so a paused budget does not hold workers.
- `RateBudgets.Throttled(...)` (since 2.2 `ThrottledAsync(permit, ...)`): a `429` pauses the budget and drains it, for `Retry-After`, or with a doubling backoff when there is none.
- Recipes: `"rate": { "budget": "...", "key": "..." }`. Every request to the API waits for a token; a `429` pauses the budget.
- Metrics: time spent waiting for a token, and pauses, tagged with the budget's name only.

Gate:
- Many jobs sharing a budget against a fake server with a hidden token bucket: no `429` when the declared limit is within the real one. Without the budget, the same run gets `429`s.
- After one `429` with `Retry-After`, no request of the budget leaves before the pause ends.

**Result:** both pass (`BudgetGateTests`).
- Eight jobs share one key, 32 requests, against an API allowing 40 a second with bursts of 4 and no `Retry-After`. At 30 a second there is no `429`; without the budget the same run is throttled at once.
- Mutation checks turn the tests red: removing the pause on `429`, or the wait for a token.

**Added on the way:** `JobPostponedException`. Without it a job that gives its slot back would use an attempt, and a long pause would abandon jobs that never sent a request.

### 2.2 Shared across instances (merged)

- Migration 2: `rate_buckets`, one row per budget and key. It only adds a table, so an instance of the previous version keeps working during a rolling deploy.
- Every token is one statement that locks the row. Batches were planned, then dropped after measuring: see [rate-budgets.md](rate-budgets.md#where-the-tokens-live).
- A pause is written to the row, so every instance sees it. A request that waited reads the pause count once more before it goes.
- `WaitAsync` returns a `BudgetPermit`; `ThrottledAsync` takes it back. A `429` for a request sent before the latest pause no longer doubles the backoff again (found while building this part: it was a bug in 2.1).

Gate: two hosts sharing one budget never exceed it together.

**Result:** passes (`BudgetGateTests`).
- Two hosts with eight slots each, sixteen jobs of four pages, one key, a budget of 30 a second, against an API allowing 40 a second with bursts of 4: no `429`, both hosts sent requests, and no second held more than 32 arrivals. With a key per host instead, the same run is throttled at once.
- A `429` with `Retry-After: 1` heard by one host: neither host sends during the pause.
- Mutation checks turn tests red: removing the row lock (instances take more tokens than the bucket holds), removing the pause check after a wait, and counting every `429` as new.
- Measured: about 0.16 ms per token; about 12,000 tokens a second on one key with eight callers (local PostgreSQL 18).

### 2.3 Reading what the API says (in review)

What changed from the plan, after analysis:
- **The IETF field is opt-in, not read by default.** Its draft has changed shape twice, and an API may count a partition other than the budget's key. A recipe names it like any other header.
- **Its `t` is not a reset.** Draft 11 defines it as a window within which the quota must not be exceeded, and says a client "MUST NOT assume that all its service limit will be fully restored". So after any header-driven pause the bucket restarts empty, never full.
- **A `403` can be a throttle.** GitHub answers a spent limit with `403` and `x-ratelimit-remaining: 0`; until now such a job was abandoned.
- **The delegating handler moves to a later part.** It needs the current job's owner without being told, which is a new engine API. Hand-written handlers call `RateBudgets.ReportRemainingAsync` meanwhile.

Built:
- `rate.headers` in recipes: `remaining` (`number`, `used/limit`, `ietf`), `reset` (`seconds`, `unix`, `unix-ms`, `http-date`), measured against the answer's `Date`.
- Readings only lower a budget, and subtract the requests that took a token after the one answered (migration 3: a `taken` counter).
- Nothing left with a reset ahead pauses the key on every instance until the reset; counted as `synclane.budget.exhausted`, not as a throttle.
- A `429` without `Retry-After` waits for the reset, at least a second.

Gate, restated: the plan asked for "each pattern without a `429`". That holds where the API says when quota comes back. Where it only says how much is left, no client can promise it while another app spends the same quota between two answers, or while the declared rate is above the real one. So:
- windowed patterns (seconds to reset, Unix reset with `403`, IETF): no `429` once declared, where the same run without headers is throttled;
- count-only patterns: measured, and documented as an improvement, not a guarantee.

**Result:**
- `HeaderGateTests`: four jobs, 30 ms network latency, an API with 8 requests a second of which another app spends 2, a budget declaring the full 8 at once. With headers: no `429` or `403` in all three patterns, five runs in a row. Without: throttled at once.
- Mutation checks turn tests red: no subtraction of requests in flight (gate, two patterns), no pause at zero, readings not reported, `403` never a throttle.
- Count-only, measured: hidden bucket of 6 a second, budget of 8, four jobs, six seconds. Without headers 22 served and 12% throttled; with headers 28 served and 7 to 10% throttled. The count fixes the bucket's level, not its rate; part 2.4 is what lowers the rate.

### 2.4 Learning a limit

- For budgets declared without a limit, or with `learn`: start low, halve on `429`, climb slowly after a quiet window, never above a declared ceiling.
- A benchmark against a fake server with a hidden limit.

Gate: the phase gate above.

## Checks against what we refuted

| Refuted idea | Question for the pull request |
| --- | --- |
| Finding the best rate from a curl | Does it claim to know a provider's limit? Budgets are declared by the user, or learned from answers, never assumed. |
| Platform knowledge inside the core | Does `src` name a platform? Provider headers are configured in recipes. |
| SyncLane as a middleman | Does any budget state leave the user's process and database? |
| Probing a limit by going fast | Does learning ever start high? |
