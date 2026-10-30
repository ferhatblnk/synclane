# Design: rate budgets

Status: proposal, not built. Phase 2 of the roadmap. Written before the code so the decisions can be argued with.

## The problem

Today a lane does two jobs at once. It says "these jobs must not run at the same time", and the README also suggests it for "these accounts share a rate limit". The second use is too blunt. A lane runs one job at a time, so two accounts that share a limit of 1,000 requests a minute are pushed into a single file even when the limit would let them both run at full speed.

Real limits also rarely match a lane:

- **Per operation, per account and app.** Amazon's Selling Partner API uses a token bucket per operation and per selling partner and application pair. Some plans change with the seller's business, not with how often the app calls. ([Usage plans and rate limits](https://developer-docs.amazon.com/sp-api/docs/usage-plans-and-rate-limits))
- **Per group of endpoints, per seller tier.** Since 14 September 2026 Trendyol counts product calls against shared groups (read, write, stock and price), with limits that grow with the seller's listing tier, plus 30 requests a minute per barcode for price updates. ([Servis Limitleri](https://developers.trendyol.com/docs/1-servis-limitleri))
- **Unknown.** Many APIs publish no number at all and answer `429` when you go too fast.

## Decision

Keep the lane for exclusivity. Add a **rate budget** as a separate idea.

| | Lane | Rate budget |
| --- | --- | --- |
| Question it answers | May this job run now? | May this request go out now? |
| Granularity | One job | One HTTP request |
| Enforced by | Partial unique index on running jobs | Token bucket |
| Scope | `(queue, lane)` | `(budget name, budget key)` |

Two jobs in different lanes that share a budget run side by side and draw from one bucket.

## Shape of the API

```csharp
lane.AddQueue("shops", queue =>
{
    queue.Budget("orders-read", limit: 100, per: TimeSpan.FromMinutes(1));
    queue.Budget("catalog-read", limit: 2000, per: TimeSpan.FromMinutes(1), learn: true);

    queue.Windowed<OrderHandler>("orders", /* ... */).Uses("orders-read");
    queue.Snapshot<ProductHandler>("products", /* ... */).Uses("catalog-read");
});

builder.Services.AddHttpClient<ShopApi>().AddSyncLaneBudget();
```

- **The budget key** is the owner's `RateKey`, which defaults to the lane. Accounts that share one set of credentials give it the same key.
- **`AddSyncLaneBudget()`** is a `DelegatingHandler`. While a page runs, the runner puts the job's budget and key in an `AsyncLocal` context; the handler takes one token before each request and waits if there is none. Code-first handlers get budgets without changing a line, and the future declarative HTTP source uses the same path.
- A request made outside a page (no context) passes through untouched.

## Where the tokens live

Several instances share one budget, so a bucket in memory is not enough on its own. Three options:

1. **Split the limit by instance count.** Simple, but needs to know how many instances run, and wastes the share of an idle one. Rejected.
2. **One database round trip per request.** An atomic `UPDATE … RETURNING` on a `rate_buckets` row. Exact, but adds a round trip to every call. At 100 a minute that is nothing; at 50 a second per key it is too much.
3. **Reserve tokens in small batches.** An instance takes up to `n` tokens from the row in one statement and spends them in memory. Unused tokens expire with the window. **Chosen**, with `n` adaptive: 1 when the limit is low, more when it is high.

A single-instance mode (`queue.Budgets = BudgetStore.InMemory`) skips the table and uses `System.Threading.RateLimiting` directly.

This needs migration 2: `rate_buckets (queue, budget, key, tokens, refilled_at, paused_until, learned_limit)`.

## Learning a limit

When `learn` is on, or no limit is declared:

- Start at a conservative rate (a setting, default 1 request a second).
- On `429`, or on a `JobThrottledException`: halve the rate and pause the bucket for `Retry-After` if the response named one.
- After a full window without a throttle: raise the rate by 10%.
- Never go above a declared limit. A declared limit is a ceiling, learning only finds out how much of it is safe.

Headers read, in order of trust:

1. `Retry-After`, seconds or an HTTP date. Standard.
2. `RateLimit` and `RateLimit-Policy` from the IETF draft. Still an Internet-Draft, so it is read but not relied on. ([draft-ietf-httpapi-ratelimit-headers](https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-ratelimit-headers))
3. `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`. Common but not standard; meanings differ between providers.
4. Provider headers such as `x-amzn-RateLimit-Limit`, which Amazon says may be missing and must not be depended on.

## What it will not do

- **Probe for the limit by going fast on purpose.** It is someone's seller account. Learning only ever starts low and climbs.
- **Guarantee zero `429`.** Other apps on the same account draw from the same limit on the provider's side, and we cannot see them.

## How we know it works

A fake server with a hidden limit, run in the test suite:

- With a declared limit: no `429` at all.
- With learning and no declared limit: `429` below 1% of requests and throughput at least 80% of the hidden limit after the first five windows.
- Two instances sharing one budget never exceed the limit together.

These thresholds are ours, set in the roadmap; they are not an industry standard.

## Open questions

- Should `Uses()` allow several budgets for one type, for a page that calls two endpoint groups? Probably yes, with the budget picked per request by a URL rule.
- Is `RateKey` on `JobOwner` the right place, or should the budget key be a function of the owner?
- What does `synclane.job.wait` mean once a job can be running but waiting for tokens? Likely a new metric, `synclane.budget.wait`.
