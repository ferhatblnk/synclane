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

### 2.1 Budgets in one process (in review)

- `AddBudget(name, limit, per, burst)` on the engine's options, shared by every queue.
- `RateBudgets.WaitAsync(budget, key)`: a token bucket of our own, on `TimeProvider`.
  - A request that would wait longer than `MaxBudgetWait` gives its job's slot back instead (`JobThrottledException`), so a paused budget does not hold workers.
- `RateBudgets.Throttled(...)`: a `429` pauses the budget and drains it, for `Retry-After`, or with a doubling backoff when there is none.
- Recipes: `"rate": { "budget": "...", "key": "..." }`. Every request to the API waits for a token; a `429` pauses the budget.
- Metrics: time spent waiting for a token, and pauses, tagged with the budget's name only.

Gate:
- Many jobs sharing a budget against a fake server with a hidden token bucket: no `429` when the declared limit is within the real one. Without the budget, the same run gets `429`s.
- After one `429` with `Retry-After`, no request of the budget leaves before the pause ends.

**Result:** both pass (`BudgetGateTests`).
- Eight jobs share one key, 32 requests, against an API allowing 40 a second with bursts of 4 and no `Retry-After`. At 30 a second there is no `429`; without the budget the same run is throttled at once.
- Mutation checks turn the tests red: removing the pause on `429`, or the wait for a token.

**Added on the way:** `JobPostponedException`. Without it a job that gives its slot back would use an attempt, and a long pause would abandon jobs that never sent a request.

### 2.2 Shared across instances

- Migration 2: `rate_buckets`. Instances reserve tokens in small batches in one statement; a pause is written to the row so every instance sees it.

Gate: two hosts sharing one budget never exceed it together.

### 2.3 Reading what the API says

- `RateLimit` and `RateLimit-Policy` from the IETF draft, read but not relied on.
- Per recipe, the headers that say how much is left and when it refills, with their formats (seconds, Unix time, HTTP date):
  - stop at zero and wait for the reset, instead of running into a `429`;
  - this is the exact answer to fixed windows.
- A delegating handler, so hand-written handlers get budgets without changing a line.

Gate: fake servers built on each pattern in the table above, each run without a `429` once its headers are declared.

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
