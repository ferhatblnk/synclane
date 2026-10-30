# CLAUDE.md

Instructions for anyone changing this repository, human or AI. Read [ARCHITECTURE.md](ARCHITECTURE.md) for why things are the way they are; this file says what to do.

SyncLane is a .NET library that runs sync jobs against other people's APIs from one PostgreSQL table. It is a developer kit: it runs inside the user's process, with the user's credentials, against the user's database. Everything below follows from that.

## Commands

```bash
dotnet build                      # warnings are errors, missing XML docs on public API included
dotnet test                       # PostgreSQL tests are skipped without SYNCLANE_TEST_DB
SYNCLANE_TEST_DB="Host=localhost;Username=postgres;Database=synclane_test" dotnet test
dotnet test --filter "FullyQualifiedName~ConventionTests"   # the rules below that a machine checks
```

Run the PostgreSQL tests before calling any change to storage, running or the public API done. Each test creates and drops its own schema; any empty database works.

`docs/schema.sql` is generated. After adding a migration, regenerate it from `SyncLaneSchema.Script()`; `OptionsTests` fails until you do.

## Layout

| Path | What lives there |
| --- | --- |
| `src/SyncLane/*.cs` | Public API: `ISyncLane`, options, `Job`, `JobQuery`, `SyncLaneSchema`, `SyncLaneTelemetry`. |
| `src/SyncLane/Errors` | `SyncError`, `SyncErrorKind`, `SyncErrorCodes`, `SyncLaneException` and the message catalog `SyncLaneErrors`. |
| `src/SyncLane/Results` | `SyncResult`, `SyncResult<T>`, `SyncListResult<T>`. |
| `src/SyncLane/Configuration` | Options and their validation. |
| `src/SyncLane/Planning` | What jobs should exist. Pure functions plus one read and one insert. |
| `src/SyncLane/Storage` | Every SQL statement (`JobSql`), table access (`JobStore`), migrations. |
| `src/SyncLane/Running` | Workers, the page loop, the failure policy. |
| `src/SyncLane/Telemetry` | Metrics instruments and outcome names. |
| `src/SyncLane.Http` | The declarative HTTP source: recipes, request building, record paths, paging, authentication, its own codes (`HttpErrorCodes`) and catalog (`HttpErrors`). Uses only the engine's public API. |
| `tests/SyncLane.Tests` | Unit tests, PostgreSQL tests (`[PostgresFact]`) and `ConventionTests`. |
| `docs` | User documentation, `errors.md`, design proposals in `docs/design`. |

## Rules

Each rule has a reason. If a rule is in your way, change the rule here first, with its reason, in the same pull request. Rules marked **(checked)** are enforced by `ConventionTests`.

### 1. Dependencies

- The engine depends on the BCL, `Npgsql` and `Microsoft.Extensions.*.Abstractions`. Nothing else. `SyncLane.Http` adds `Microsoft.Extensions.Http` (decision D13).
- No platform is named in `src`: not in code, not in comments. Platform knowledge belongs in recipes. **(checked)**
- **No in-house or company packages in this solution.** Nothing private, from any employer or client. SyncLane is public and must build for anyone. An adapter to such a package (for example mapping `SyncResult<T>` to `ApiResult<T>`) lives in a separate solution that references SyncLane, never the other way round.
- `SyncLane.Http` uses only the engine's public API. If it needs something internal, make a small, read-only public API in the engine instead (as `SyncLaneOptions.Describe()`).
- Adding any dependency needs an entry in the decision log of ARCHITECTURE.md: what it does, why the BCL is not enough, its license, and who maintains it.

### 2. Errors

Every failure falls into exactly one category. Pick the mechanism from the category, never by taste.

| Category | Example | Mechanism |
| --- | --- | --- |
| Expected outcome of a call | Undeclared queue, duplicate job, limit out of range | Return a failed `SyncResult` with a catalog error. Never throw. |
| Configuration | Missing connection string, bad schema name, missing migration | Throw `SyncLaneException` with a catalog error, at start-up. |
| Bug in the caller | `null` argument | `ArgumentNullException.ThrowIfNull` and the other BCL throw helpers. |
| Infrastructure | Database down, network error | Let the exception propagate. Inside a worker it ends the attempt and the job is retried. |
| Handler failure | The user's handler throws | Stored in the job's `error` column; the failure policy decides the retry. |
| Cancellation | Token cancelled | `OperationCanceledException`. Never a failed result, never logged as an error. |
| Impossible state | A status the code does not know | Throw `SyncLaneException` with a `SyncErrorKind.Internal` error. |

- **All messages come from `SyncLaneErrors`.** Never write an error message in place: no `new XException("...")` anywhere in `src`. **(checked)**
- Every message says what went wrong and how to fix it, in plain English, naming the setting, queue or type involved.
- Never put a secret, a token, a header value or a response body in a message.
- To add an error: add a constant to the package's code list (`SyncErrorCodes` or `HttpErrorCodes`) with the next free number in its range, a factory to its catalog (`SyncLaneErrors` or `HttpErrors`), and a row to `docs/errors.md` with cause and fix. **(checked: every code has a factory and a row, and every row has a code)**
- Codes are public contract. Never renumber, reuse or remove one. Rewording a message is fine.
- `catch (Exception)` only where the exception is turned into state (a job's status) or logged and the loop continues. Never swallow silently. Use `when` filters to keep cancellation out.

### 3. Results

- Every method on `ISyncLane`, and every future public operation that can fail for an expected reason, returns `SyncResult`, `SyncResult<T>` or `SyncListResult<T>`.
- A result is immutable. Build it with `Success`/`Failure`; never add setters.
- A list is paged with a cursor (`NextCursor`), never an offset. `Total` is computed only when the query asks for it.
- Do not return a failed result for a bug or an infrastructure failure. A result that can mean "the database is down" teaches callers to ignore failures.

### 4. Async

- **Do not await inside a loop over independent items.** Put the work in one statement (`unnest` arrays, `NpgsqlBatch`), or start it all and `await Task.WhenAll(...)`, bounded when the count is not small.
- A loop may await in sequence only when the order is the point. Mark it with a `// Sequential:` comment on the line above that says which case it is: **(checked)**
  - the next step depends on the previous result (pages and cursors, retries);
  - reading rows from one data reader or stream;
  - a timer or polling loop;
  - steps that must share one connection or transaction in order.
- Never block on a task: no `.Result`, `.Wait()` or `.GetAwaiter().GetResult()`. **(checked)**
- Every async method takes a `CancellationToken` and passes it on. Use `CancellationToken.None` only for the write that records how a job ended (handed back, failed, abandoned), so a stopping worker still leaves the row correct, and for saving a rotated refresh token, whose predecessor the provider may already have retired.
- Time comes from `TimeProvider`, never `DateTime.UtcNow`, so tests can move the clock.

### 5. Performance

- One round trip per logical operation. Batch inserts with `unnest`, combine statements with `NpgsqlBatch`, never query in a loop (no N+1).
- Every query that runs on a schedule or per job is served by an index. A new hot query comes with its index in a migration and a note in ARCHITECTURE.md.
- Keyset pagination, no `OFFSET`, no `COUNT(*)` unless asked for.
- No reflection, LINQ-heavy allocation or string building on the per-page path. Metrics use `TagList`.
- Telemetry must cost nothing without a listener: guard span work with `activity?.`.
- Measure before optimising anything that is not one of the above; put the numbers in the pull request.

### 6. Security

- SQL values are always parameters. The only thing ever interpolated into SQL is the schema name, and only after `SchemaName.IsValid`.
- Never log, trace, tag or store credentials, headers, request or response bodies, or cursors (cursors often contain tokens). A recipe never holds a credential, and a key in a query string is taken out of a URL before it becomes a cursor.
- Never keep the message of a network exception (`HttpRequestException` and the like): it can hold the URL or a proxy's password. Keep its kind. **(checked by `CredentialLeakTests`, which also searches logs, spans, metrics and every stored row for each auth type)**
- Metric tags carry the queue and job type only, never the owner or lane: they identify customers and have unbounded cardinality. **(checked by `TelemetryTests`)**
- The `error` column is capped at 2,000 characters. Keep it that way.
- SyncLane opens no port and calls no host on its own. No telemetry home, no license check, no update check. Ever.
- Validate input at the public boundary (`ISyncLane`, options, `SyncLaneSchema`), then trust it inside.

### 7. Design (SOLID, applied with judgement)

- **Single responsibility.** One class, one reason to change: `JobSql` owns SQL text, `JobStore` executes it, `JobPlanner` decides, `QueuePlanner` reads and writes, `JobRunner` runs one job, `JobWorker` schedules slots. Keep it so.
- **Open/closed.** New behaviour arrives as a new handler, owner source, sink or migration, not as a `switch` on a type name.
- **Liskov.** Every `SyncResult` subtype keeps the base contract: `Succeeded` exactly when `Errors` is empty.
- **Interface segregation.** Extension points are small: `IJobHandler` and `IOwnerSource` have one method each. Future ones (`ISink`, `IRequestSigner`, `ICredentialStore`) follow the same rule.
- **Dependency inversion at the seams only.** Interfaces exist where the user plugs in code. Internal classes are `internal sealed` and have no interface of their own; tests use a real PostgreSQL instead of mocks. Do not add `IJobStore` and the like.
- Classes are `sealed` and `internal` unless they are meant to be used or extended from outside.

### 8. Schema

- Migrations are append-only. Add a new one to the end of `Migrations.All`; never edit a released one.
- Every migration is safe to run twice (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`), and only adds, so an older instance keeps working during a rolling deploy.
- Regenerate `docs/schema.sql` afterwards.

### 9. Public API and documentation

- Every public member has an XML doc comment; the build fails without one.
- A change to public behaviour updates, in the same change: the README section that describes it, `docs/*` where relevant, `docs/errors.md` for new codes, and `CHANGELOG.md`.
- Breaking changes are allowed before 1.0 and must be listed under the version in `CHANGELOG.md`.

### 10. Tests

- Test names are sentences that state the behaviour: `A_failed_job_runs_again_from_the_page_it_stopped_at`.
- Anything touching SQL is tested against PostgreSQL with `[PostgresFact]`, not mocked.
- Every bug fix comes with a test that fails without it.
- Never put real connection strings, tokens, account ids or customer data in tests or fixtures.

## Style

- File-scoped namespaces, primary constructors, `record` for data, `required` for mandatory init properties.
- Logging through `[LoggerMessage]` source-generated methods, with messages that read as sentences.
- Comments explain why, not what. A comment that restates the code is deleted.
- English in code, comments, messages and documentation.

## Before you finish a change

- [ ] `dotnet build` passes with no warnings.
- [ ] `dotnet test` passes with `SYNCLANE_TEST_DB` set.
- [ ] New errors have a code, a catalog entry and a row in `docs/errors.md`.
- [ ] New awaited loops are batched, parallel, or marked `// Sequential:`.
- [ ] Docs and `CHANGELOG.md` describe the change.
- [ ] No secret, header, body or cursor reaches a log, a trace, a tag or a message.
