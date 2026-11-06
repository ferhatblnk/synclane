# Error codes

Every error SyncLane reports has a stable code. A code is never reused or renumbered, so it is safe to branch on, to alert on and to use as a localization key. The message that comes with it is English and may be reworded; the code does not change.

The first digit is the area:

| Range | Area | How it reaches you |
| --- | --- | --- |
| `SL1xxx` | Configuration | `SyncLaneException` from `AddSyncLane`, before the application starts. |
| `SL2xxx` | Schema and stored data | `SyncLaneException` at start-up, or when a row cannot be read. |
| `SL3xxx` | Calls to `ISyncLane` | A failed `SyncResult`, never an exception. |
| `SL4xxx` | Running jobs | Text in the job's `error` column, starting with the code. |
| `SL5xxx` | The HTTP source (`SyncLane.Http`) | `SL50xx`: a failed result when a recipe is loaded. `SL51xx`: a recipe that does not fit a job or a response, while running. |
| `SL6xxx` | The `synclane` command (`SyncLane.Cli`) | A curl or a recipe the command cannot work with. |

`SyncErrorCodes` and `HttpErrorCodes` hold every code as a constant. A test checks that each one is listed on this page.

## Configuration

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL1001` | Configuration | No connection string was set. | Set `options.ConnectionString`, for example from configuration. |
| `SL1002` | Configuration | The schema name is not a plain lower-case identifier. It is placed into SQL as an identifier, so anything else is refused. | Use lower-case letters, digits and underscores, starting with a letter or underscore. |
| `SL1003` | Configuration | `PlanInterval` or `PollInterval` is zero or negative. | Use a positive interval. |
| `SL1004` | Configuration | Two queues have the same name. | Give each `AddQueue` call its own name. |
| `SL1005` | Configuration | Two job types of one queue have the same name. | Give each type of a queue its own name. |
| `SL1006` | Configuration | A queue setting cannot work, such as `Concurrency = 0` or a `RetryMaxDelay` below `RetryBaseDelay`. The message names the setting and the rule. | Change the setting as the message says. |
| `SL1007` | Configuration | A job type setting cannot work, such as a zero `slice` or an `offset` longer than `every`. | Change the setting as the message says. |
| `SL1008` | Configuration | A rate budget is declared twice with `AddBudget`. | Declare each budget once; every queue can draw from it. |
| `SL1009` | Configuration | A rate budget's setting is out of range: the name, `limit` or `burst` below 1, or `per` not longer than zero. | Change the setting the message names. |
| `SL1010` | Configuration | `options.Application`, or the host's application name it defaults to, is empty, longer than 100 characters, or holds a character other than letters, digits, dots, underscores, colons, slashes or hyphens. | Set `options.Application` to a name that fits. |
| `SL1011` | Configuration | At start-up, a live instance of another application already runs a queue this one runs, with its planner or its workers. Two applications on one queue would take each other's jobs and abandon the types they do not know. The message names the queue, the application and the instance. | Rename the queue in one of the applications, or give both the same `options.Application` if they are one application. A stopped instance never counts; one that died without stopping stops counting two minutes after its last heartbeat. |
| `SL1012` | Configuration | The section passed to `AddSyncLane(configuration, ...)` names a queue, job type or budget the code does not declare, or a setting SyncLane does not have there. Configuration overrides what the code declares and never adds to it. Names ignore case, as configuration keys do, unless the code declares two names that differ only in case. The message gives the full path and what can be set there. | Fix the name, or declare the queue, type or budget in code first. |
| `SL1013` | Configuration | A value in that section cannot be read as what its setting takes: a whole number, a time span such as `00:15:00` or `7.00:00:00`, `true` or `false`, or a number such as `0.25`. The message names the path and the expected kind, never the value, which could be a connection string. | Fix the value, or remove it to keep what the code sets. |

## Schema and stored data

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL2001` | Configuration | `CreateSchema` is off and the database lacks a migration this version needs. | Run `SyncLaneSchema.Script()` in your own migration, or turn `CreateSchema` on. |
| `SL2002` | Internal | A row holds a status this version does not know. It was written by a newer version or by hand. | Run the same version on every instance, or correct the row. |
| `SL2003` | Internal | A row holds a mode this version does not know. | The same as `SL2002`. |
| `SL2004` | Internal | The row of a rate bucket was deleted while an instance was using it. | Do not delete rows from `rate_buckets` while instances run. A deleted row is created again, full, on next use; the job is retried. |

## Calls to ISyncLane

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL3001` | NotFound | The call names a queue that was not declared. | Declare the queue in `AddSyncLane`, or check the name. |
| `SL3002` | NotFound | `EnqueueAsync` names a job type that is not declared on the queue. | Declare the type with `Snapshot`, `Windowed` or `Handle`. |
| `SL3003` | Validation | `JobQuery.Limit` is below 1 or above `JobQuery.MaxLimit`. | Ask for a smaller page and follow `NextCursor`. |
| `SL3004` | Validation | `JobQuery.Cursor` was not produced by SyncLane. | Pass the previous page's `NextCursor` unchanged. |
| `SL3005` | Configuration | `RateBudgets.WaitAsync` or `Throttled` was called with a budget that is not declared. | Declare it with `lane.AddBudget(...)`, or correct the name. |
| `SL3006` | Validation | `MarkLoadedAsync` was given a job type that is not declared with `Windowed`. A snapshot type reads everything on each run, so there is no history to mark. | Mark only windowed types; leave snapshot types to the planner. |
| `SL3007` | Validation | A range given to `MarkLoadedAsync` starts after it ends, ends in the future, or, for a type without `every`, ends before today (UTC), which would leave the days after it unloaded for good. Nothing is marked. | Correct the range. For a type without `every`, mark history up to today. |
| `SL3008` | NotFound | `RequeueJobAsync` or `RestartJobAsync` names a job that does not exist. Clean-up deletes completed jobs after `KeepCompletedJobs`, and `ForgetOwnerAsync` deletes an owner's jobs. | Check the id against `GetJobsAsync`. |
| `SL3009` | Validation | `PauseAsync` was given an end (`until`) that is not after now. Nothing was paused. | Give a later end, or none to keep the pause until `ResumeAsync`. |
| `SL3010` | Validation | `LimitBudgetAsync` was given a limit below 1, a period of zero or less, or an end (`until`) that is not after now. Nothing was changed. | Give at least 1 request per a positive period, and a later end or none. To lift a limit, use `ClearBudgetLimitAsync`. |
| `SL3011` | Validation | `JobQuery.Search` or `FreshnessQuery.Search` is empty, only spaces, or longer than `JobQuery.MaxSearchLength` (100) characters. Nothing was read. | Search for part of an owner's key or name, or leave `Search` out to list every owner. |
| `SL3012` | NotFound | `SyncNowAsync` was asked for an owner and job type that the queue has never planned, so there is no coverage to sync from. Nothing was created. | Check the queue, owner and type against `GetFreshnessAsync`. A new owner is planned by the application that runs the queue; call `PlanAsync` there, or wait for the next planning round. |
| `SL3013` | Validation | An owner's `Values` hold a name that is not letters, digits and underscores starting with a letter, the name `key`, more than 20 values, or a value with a control character or over 1,000 characters. The message names the value, never shows it. `EnqueueAsync` and `PlanAsync` return it; a planning round that meets it fails and is tried again. | Fix the values where they are made: the owner source, or the `NewJob`'s owner. |
| `SL3014` | Validation | `NewJob.RequestedBy` is longer than 200 characters or has a control character such as a line break. Nothing was enqueued. | Give a short identifier of who or what asked, such as a user's id or `scheduler`. |
| `SL3101` | Conflict | `EnqueueAsync` found a job with the same owner, type and dedup key. | Requeue the existing job, or use another key. |
| `SL3102` | Conflict | The job's status does not allow the action: a requeue takes failed, deferred and abandoned jobs, a restart takes any job that is not running. Nothing was changed. | Wait for a running job to end, or use the action that fits its status. |

## Running jobs

These are written to the `error` column of the job, which ends `abandoned`.

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL4001` | Configuration | A worker took a job whose type has no handler in that process, usually because instances run different versions or configurations. | Declare the type on every instance that runs workers, then requeue. |
| `SL4002` | Internal | The job was taken more often than its attempts allow without ever reporting a result. Its worker most likely dies while running it. | Look for crashes or out-of-memory kills during this job, fix them, then requeue. |
| `SL4003` | Throttled | A request would have waited for a rate budget's token longer than `MaxBudgetWait`, so the job gave its slot back. The attempt does not count, the job keeps its cursor and runs again when the token is due. Written to the `error` column while the job waits. | Nothing, if it is rare. If it is common, the budget is smaller than the work: raise its limit if the API allows, or plan fewer jobs. |

## The HTTP source

Recipes are checked when they are loaded, and every problem is reported at once. Messages name the recipe and the field, never a header value or a credential.

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL5001` | Validation | The recipe is not valid JSON, misses `name` or `request`, has a field the format does not know (often a typo), or a value of the wrong type. The message says which. | Correct the JSON; see [recipes.md](recipes.md). |
| `SL5002` | Validation | A required field is empty, such as `name` or `request.url`. | Fill it in. |
| `SL5003` | Validation | `request.url` is not absolute, does not use https, or contains a user name or password. | Use an absolute https URL. Put credentials in headers. Set `allowHttp` only for a local test server. |
| `SL5004` | Validation | A placeholder names a value that does not exist. The message lists the ones that do. | Use `{{window.start}}`, `{{window.end}}` or `{{owner.key}}`. |
| `SL5005` | Validation | A placeholder appears in the scheme, host or port of the URL, and the recipe lists no `request.hosts`. A recipe may only call the hosts it names. | Write the host out, and use the path or the query for values that change. For a host per owner, list every host it may be in `request.hosts` and make it from the owner's values. |
| `SL5006` | Validation | `records` is not `$` or `$.name.name`. Filters, indexes and `..` are not supported. | Point the path at the array of records by property names. |
| `SL5007` | Validation | `window.format` is not a known format. | Use `iso8601`, `unix`, `unixMilliseconds` or `date`. |
| `SL5008` | Validation | `request.method` is not GET or POST. | Use GET or POST. |
| `SL5009` | Validation | A header name is not an HTTP token, is one SyncLane sets itself (`Host`, `Content-Length`, `Transfer-Encoding`, `Connection`), or a value has a line break. | Correct or remove the header. |
| `SL5010` | Validation | `request.body` is set on a GET. | Use POST, or remove the body. |
| `SL5011` | Validation | A placeholder is unclosed or empty, such as `{{window.start` or `{{}}`. | Close it: `{{window.start}}`. |
| `SL5012` | Configuration | A setting of `AddSyncLaneHttp` cannot work, such as a zero `Timeout`. | Change the setting as the message says. |
| `SL5013` | Configuration | A recipe was registered without a sink. | Add `.WriteTo<TSink>()` or `.WriteTo((page, cancellationToken) => ...)` after `Recipe(...)`. |
| `SL5014` | Configuration | A job type runs with `HttpRecipeHandler` and has no recipe. Found at start-up, or by the handler if it runs outside a host. | Register a recipe for the type in `AddSyncLaneHttp`. |
| `SL5015` | Configuration | A recipe is registered for a job type that no queue declares with `HttpRecipeHandler`, usually a typo. | Correct the type name on one side. |
| `SL5016` | Configuration | Two recipes are registered for one job type. | Keep one. |
| `SL5017` | Validation | The pagination cannot work as written: a required field is missing, a field does not apply to the type, a size or start is out of range, a path is not supported, or the paging parameter is also set in `request.query`. The message names the field. | Change the field as the message says; see the pagination types in [recipes.md](recipes.md#pagination). |
| `SL5018` | Validation | The authentication cannot work as written: a required field is missing, a field does not apply to the type, the token URL is not an absolute https URL or has a placeholder, or the API key's name clashes with a header, query or paging parameter. The message names the field. | Change the field as the message says; see [Authentication](recipes.md#authentication). |
| `SL5019` | Validation | `request.headers` sets `Authorization` or `Proxy-Authorization`. A recipe is meant to be shared and committed, so it never holds a credential. | Remove the header and use `auth`; the credential store gives the value for each owner. |
| `SL5020` | Configuration | A recipe uses `auth` and no credential store is registered. | Add `http.Credentials<TStore>()` with a class that implements `ICredentialStore`. |
| `SL5021` | Configuration | A recipe has `auth.type` `signed` and names a signer that is not registered. | Add `http.Signer<TSigner>("name")` with a class that implements `IRequestSigner`, or correct `auth.signer`. |
| `SL5022` | Validation | The recipe's `rate` cannot work as written: `budget` is not a valid name, or `key` is malformed or uses a placeholder other than `{{owner.key}}`. | Change the field as the message says; see [Rate budgets](recipes.md#rate-budgets). |
| `SL5023` | Configuration | A recipe draws from a budget that is not declared. Found at start-up. | Add `lane.AddBudget("name", ...)` in `AddSyncLane`, or correct `rate.budget`. |
| `SL5024` | Configuration | A Postgres destination's setting cannot work: the table or schema is not a lower-case identifier, the connection string is empty, or a key or version path is not written as `$.name`. | Correct the setting in `WriteToPostgres`. |
| `SL5101` | Configuration | The recipe uses `{{window.start}}` or `{{window.end}}`, and the job has no window. | Declare the job type with `Windowed`, or remove the window placeholders. |
| `SL5102` | Validation | A header would contain a line break once its placeholders were filled, usually from an owner key. The request is not sent. | Remove line breaks from the owner key. |
| `SL5103` | Configuration | The response has nothing at the `records` path. | Compare the path with a real response. |
| `SL5104` | Configuration | The `records` path leads to an object or a value, not an array. | Point the path at the list itself. |
| `SL5105` | Throttled | The API answered 429, or 403 with `Retry-After` or with a declared remaining header of 0. The job waits for `Retry-After`, cut to `MaxRetryAfter`, or for the reset the recipe's rate headers give, or for the queue's throttle delay. With a rate budget the job is postponed for the budget's pause and the attempt does not count. | Nothing, unless it keeps happening: then lower `Concurrency` or widen `every`. |
| `SL5106` | Unauthorized | The API answered 401, or 403 without a sign of a spent limit. The job is abandoned at once: the same credentials will not work on the next try. | Fix the owner's credentials, then requeue. |
| `SL5107` | Unavailable | The API answered 408 or a 5xx status. The job is retried with the queue's growing delay. | Nothing, unless it keeps happening. |
| `SL5108` | Unavailable | The API did not answer within `Timeout`. The job is retried. | Ask for smaller pages, or raise `Timeout`. |
| `SL5109` | Unavailable | The API could not be reached. The message says what failed: the host name, the connection, TLS, the proxy, or the HTTP exchange. The exception's own message is not kept, because it may hold the URL or a proxy's password. The job is retried. | Check the host name, the network and the proxy. |
| `SL5110` | Configuration | The API answered another 4xx, such as 400, 404 or 422. Retrying would get the same answer, so the job is abandoned. | Compare the recipe with the API's documentation, then requeue. |
| `SL5111` | Configuration | The API answered with a redirect. SyncLane follows none, so a recipe only calls the host it names and headers never reach another host. | Put the final URL in the recipe. |
| `SL5112` | Configuration | The response is larger than `MaxResponseBytes`. The job is abandoned. | Ask for smaller pages, or raise `MaxResponseBytes`. |
| `SL5113` | Unavailable | The response is not valid JSON, often because it was cut off. The job is retried. The body is not quoted in the message. | Nothing, unless it keeps happening: then check what the API returns. |
| `SL5114` | Configuration | The next page's URL, from the answer, the `Link` header or the job's cursor, points to another scheme, host or port. It is not called, so the recipe's headers never reach another host. The job is abandoned. | If the API really moves between hosts, write a recipe per host. |
| `SL5115` | Configuration | The job's cursor is not one the recipe's pagination can read, usually because the pagination type changed while the job was halfway. | Clear the job's cursor and requeue it; it starts from the first page. |
| `SL5116` | Configuration | The cursor, next URL or has-more flag in the answer has the wrong JSON type. | Compare the pagination paths with a real response. |
| `SL5117` | Configuration | The answer points to the page that was just read. Following it would loop forever. | Check that the recipe sends the cursor in the parameter the API reads. |
| `SL5118` | Configuration | A job reached `MaxPagesPerJob` pages with page or offset paging. The API may ignore the page parameter. | Check the parameter, cut smaller slices, or raise `MaxPagesPerJob`. |
| `SL5119` | Unauthorized | The credential store returned no credential for the job's owner and the recipe's `auth.credential`. The job is abandoned. | Connect the owner's account, then requeue the job. |
| `SL5120` | Configuration | The owner's credential lacks a value the auth type needs, such as `Token` for `bearer` or `ClientSecret` for client credentials, or holds one a header cannot carry, such as a line break. The message names the value, never shows it. The job is abandoned. | Fix the credential in the store, then requeue the job. |
| `SL5121` | Unauthorized | The OAuth 2.0 token endpoint refused to issue a token (400, 401, 403 or another 4xx). The message adds the RFC 6749 error code, such as `invalid_grant`, when the endpoint gave one; nothing else from its answer. The job is abandoned. | Fix the client id, secret or refresh token, or the scope; for a revoked refresh token, have the owner connect again. Then requeue the job. |
| `SL5122` | Unavailable | The OAuth 2.0 token endpoint answered 408, 429 or 5xx, did not answer within `Timeout`, or could not be reached. The job is retried; after 429 it waits as `Retry-After` asks. | Usually nothing; it passes. If it keeps happening, check `auth.tokenUrl` and the network. |
| `SL5123` | Configuration | The token endpoint answered 2xx, but not with a usable bearer token: no `access_token`, a `token_type` other than bearer, a token a header cannot carry, an answer larger than 64 KiB or not JSON. The job is abandoned. | Check `auth.tokenUrl` and `auth.grant` against the API's documentation. |
| `SL5124` | Configuration | An `IRequestSigner` changed the method, scheme, host or port of the request. The request was not sent and the job is abandoned. | Change the signer to add headers or query parameters only. |
| `SL5125` | Validation | A record has no value its destination can key it by: the key is missing, not a string or a number, or, for the inferred `$.id`, not unique in the page. The message lists fields that could serve. Nothing of the page is written. | Name the key with `KeyedBy(...)`, or use `KeyedByContent()` for records that never change; then requeue. |
| `SL5126` | Validation | A record's version is neither a number nor an ISO 8601 time, so an older copy could not be told from a newer one. Nothing of the page is written. | Name another field with `VersionedBy(...)`, or leave the version out; then requeue. |
| `SL5127` | Configuration | A destination table does not exist, and `CreateSchema` is off, so SyncLane does not create it. | Run `PostgresSinkSchema.Script(schema, table)` in your own migration, or turn `CreateSchema` on; then requeue. |
| `SL5128` | Validation | An HTTP destination refused a page and will keep refusing: it answered with a redirect (not followed), `410` (endpoint gone) or another 4xx. | Fix the receiver, the URL or the configured headers; then requeue. |
| `SL5129` | Unavailable | An HTTP destination did not take a page after three tries: a 5xx, a timeout or a network failure. When it kept asking to slow down (`429`, `502`, `503`, `504`), the job is postponed without using an attempt. | Nothing, unless it lasts: then check the receiver. The job runs again from the same page. |
| `SL5130` | Validation | A single record is larger than an HTTP destination accepts: it answered `413` for that record alone, or the record is over `MaxBytes`. | Raise the receiver's limit or `MaxBytes`; then requeue. |
| `SL5131` | Validation | A sink failed a scenario of `SinkConformance.CheckAsync`: a page written again, a page with changed records, duplicates in a page, an older copy after a newer one, or the same key under two owners left the wrong records stored. Reported by the check, never at run time. | Write by key (`RecordIdentity` helps), replace a record only with a version that is not lower, and keep owners apart. |
| `SL5132` | Configuration | The recipe uses `{{owner.<value>}}` and the job's owner has no value of that name. Nothing was sent; the job is abandoned. The message names the value, never shows another. | Give the owner the value in `JobOwner.Values` from the owner source, then requeue the job. |
| `SL5133` | Configuration | The host made from the owner's values is not in the recipe's `request.hosts`, or is not a host name at all, for example a value with a `/`, `@` or `:`. Nothing was sent; the job is abandoned. | Correct the owner's value. If the API really uses that host, add it to `request.hosts`. |

## Errors that are not on this page

- **Exceptions your handler throws** are stored in the `error` column as they are, type and message, and drive the retry policy. They have no SyncLane code.
- **A `null` argument** throws `ArgumentNullException`. It is a bug in the calling code, not a condition to handle.
- **A database that cannot be reached** throws the `NpgsqlException` that Npgsql raised. Inside a worker it ends the attempt and the job is retried.
- **Cancellation** throws `OperationCanceledException`. It is never reported as a failure.

## The synclane command

| Code | Kind | What happened | What to do |
| --- | --- | --- | --- |
| `SL6001` | Validation | `synclane init` got no curl command with an http or https URL. | Type `synclane init` and paste the curl after it, as copied from the documentation or the browser's "Copy as cURL"; or pipe it in: `pbpaste \| synclane init`. |
| `SL6002` | Unavailable | The API refused the request of the curl given to `synclane init`, so there was no answer to learn from. The message says what the status usually means; a redirect is refused because recipes do not follow redirects. | Run the curl itself; copy a fresh one if the credential expired; for a redirect, put the address it redirects to in the curl. |
| `SL6003` | Validation | The API answered the curl's request with something that is not JSON. | Ask for JSON, for example with `-H 'Accept: application/json'`. |
| `SL6004` | Unavailable | The curl's request did not reach the API: the host name, the connection or TLS failed, or no answer came in time. The message names which, never the URL. | Check the URL and the connection; run the curl itself. |
| `SL6005` | Conflict | The file `synclane init` would write exists already. | Name another file with `--out`, or pass `--force` to replace it. |
| `SL6006` | NotFound | There is no recipe file at the path given to `synclane test`. | Give the path of a recipe file. |
| `SL6007` | Configuration | The recipe needs a credential, it is not in the environment, and there is no terminal to ask for it in (as in CI). | Set the variables the message names: `SYNCLANE_<CREDENTIAL>_TOKEN`, or `_USERNAME`/`_PASSWORD`, or `_CLIENT_ID`/`_CLIENT_SECRET`/`_REFRESH_TOKEN`. |
| `SL6008` | Configuration | The recipe signs its requests with an `IRequestSigner` of your application, which `synclane test` cannot load. | Run the job from your application, for example with `EnqueueAsync` and a test owner. |
| `SL6009` | Validation | The recipe uses the owner's values (`{{owner.key}}`), and no owner was given. | Pass `--owner` with the key of an owner to test with. |
| `SL6010` | Validation | Page 2 brought the same records as page 1: the API did not read the paging parameter the recipe sends. The test fails. | Check the parameter's name in the API's documentation, correct the recipe's `pagination`, and test again. |
| `SL6011` | Validation | The value of `--since` is neither a length of time nor a date. | Give a length such as `7d`, `12h` or `30m`, or a date such as `2026-10-01`. |
| `SL6012` | Validation | A tool of `synclane mcp` was given a file outside the folder the server was started in. The tools read and write recipes only there, so an assistant cannot be talked into touching other files. Nothing was read or written. | Give a path inside that folder, or start `synclane mcp` in the folder that holds the recipes (most assistants start it in the open project). |
| `SL6013` | Validation | The folder given to `synclane mcp --folder` does not exist. The server did not start. | Give the path of an existing folder, relative to where the assistant starts the server or absolute, or leave `--folder` out. |
| `SL6014` | Validation | The recipe uses values of the owner (`{{owner.marketplaceId}}` and the like), and `synclane test` was not given them. Nothing was sent. | Pass each as `--value name=value`; the message lists the names. In `synclane mcp`, give them in the test tool's `values`. |

## The HTTP endpoints

Codes of `SyncLane.AspNetCore`. They come back as problem details, with the code as `title` and in `code`.

| Code | Kind | What happened | What to do |
| --- | --- | --- | --- |
| `SL7001` | Unauthorized | A request from another machine reached endpoints mapped by `MapSyncLane` that do not require authorization. They answer only local requests until they do, and a request that came through a proxy is not local. The answer is 403. | Add `.RequireAuthorization(...)` to `MapSyncLane(...)`, with the policy your operators sign in with. |
| `SL7002` | Validation | A repair was posted without the `X-SyncLane-Action` header. The header shows the request came from your own script or page: another site cannot make a browser send it. | Send the header with any value. |
