# Plan: phase 1, the declarative HTTP source

Status: in progress. Each part is one pull request. A part is merged only when its gate passes.

## Goal

A new package, `SyncLane.Http`, that runs a job from a **recipe** instead of a hand-written handler: where to send the request, how the job's window goes into it, where the records are in the answer, how to get the next page, and how to authenticate. The engine (`SyncLane`) does not change, except where a part says so.

**Phase gate (from the roadmap):** four real APIs with different pagination styles are synced with a recipe and no custom code. **Stop rule:** if more than half of them need custom code, the declarative approach is too weak; return to code-first helpers and change the positioning.

## Where we stand

| Item | State |
| --- | --- |
| Phase 0 code (license, migrations, telemetry, results and errors, rules) | Merged, CI green |
| Phase 0 gate: 30 days in production | **Not started.** See below. |
| Design partners | None yet |

We are starting phase 1 before the phase 0 gate has passed, which the roadmap says not to do. The exception is allowed because:

- phase 1 is a new package; it does not change the engine whose behaviour the trial is meant to prove;
- the trial needs calendar time, not code, and can run alongside;
- any bug the trial finds in the engine goes ahead of phase 1 work.

If the trial has not started by the time part 1.3 is merged, phase 1 pauses until it has.

## Parts

### 1.1 Recipe model (merged)

- The recipe as C# records and as JSON, loaded with `System.Text.Json`, no new dependency.
- Validation that returns **all** problems at once as one failed `SyncResult`, each with a code in a new range, `SL5xxx`.
- Placeholders `{{window.start}}`, `{{window.end}}`, `{{owner.key}}`, rendered into the path, query, headers and body of an `HttpRequestMessage`. Nothing is sent yet.
- Finding the record array in a JSON answer with a small path syntax (`$`, `$.data.items`).

Gate: unit tests for every validation rule, every placeholder position and every window format.

### 1.2 Running a recipe (merged)

- `HttpRecipeHandler`: an `IJobHandler` that builds the request, sends it, finds the records and hands them to a sink.
- A minimal `ISink` (write one page of records) with a delegate implementation. Real sinks are phase 3.
- Responses mapped to the engine's failure policy: `429` and `Retry-After` to `JobThrottledException`; `401` and `403` to `JobAbandonedException`; `5xx` and timeouts to a retry. A cap on response size.
- New dependency `Microsoft.Extensions.Http`, recorded in the decision log.

Gate: tests against an in-process fake server; a job runs end to end through the real engine.

### 1.3 Pagination (in review)

- Four paginators behind one small interface: page or offset number, a cursor in the body, a next URL in the body, the `Link` header.
- The paginator's state is the job's cursor, so a crash continues from the page it stopped at.

Gate: each paginator against the fake server, including a crash between pages.

### 1.4 Authentication

- Static header, API key, Basic, Bearer; OAuth2 client credentials and refresh token.
- `ICredentialStore` (credentials per owner, never in the jobs table) and `IRequestSigner` for platforms that sign requests.

Gate: tests for each scheme; a test that no credential reaches a log, a span, an error or the `error` column.

### 1.5 Live check and preview

- Four public APIs, one per pagination style, synced with recipes by a sample program run by hand. Not in CI: live APIs are slow, rate limited and change.
- Package `SyncLane.Http` 0.2.0-preview, if the gate passes.

Gate: the phase gate above.

## Checks against what we refuted

Every part's pull request answers these. A "yes" needs a written reason or blocks the merge.

| Refuted idea | Question for the pull request | Guard |
| --- | --- | --- |
| A closed compression algorithm makes syncing faster | Does it add compression of our own? | Only `Accept-Encoding` with the decompression built into `HttpClient`. |
| SyncLane as a middleman that sees the data | Does it send anything to a host the user did not name? | A recipe's host is fixed text; placeholders cannot change it. |
| Finding the best rate from a curl | Does it claim to know a provider's limit? | Rate work is phase 2, and learns from responses. |
| Platform knowledge inside the core | Does `src` name a platform? | `ConventionTests` fails on platform names in `src`. |
| Exactly-once delivery | Does it promise a page is written once? | Sinks must be idempotent; documented on `ISink`. |

## Not in phase 1

Rate budgets (phase 2), real sinks (phase 3), curl import and probing (phase 4), JSON Schema and the MCP server (phase 5).
