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

## Schema and stored data

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL2001` | Configuration | `CreateSchema` is off and the database lacks a migration this version needs. | Run `SyncLaneSchema.Script()` in your own migration, or turn `CreateSchema` on. |
| `SL2002` | Internal | A row holds a status this version does not know. It was written by a newer version or by hand. | Run the same version on every instance, or correct the row. |
| `SL2003` | Internal | A row holds a mode this version does not know. | The same as `SL2002`. |

## Calls to ISyncLane

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL3001` | NotFound | The call names a queue that was not declared. | Declare the queue in `AddSyncLane`, or check the name. |
| `SL3002` | NotFound | `EnqueueAsync` names a job type that is not declared on the queue. | Declare the type with `Snapshot`, `Windowed` or `Handle`. |
| `SL3003` | Validation | `JobQuery.Limit` is below 1 or above `JobQuery.MaxLimit`. | Ask for a smaller page and follow `NextCursor`. |
| `SL3004` | Validation | `JobQuery.Cursor` was not produced by SyncLane. | Pass the previous page's `NextCursor` unchanged. |
| `SL3101` | Conflict | `EnqueueAsync` found a job with the same owner, type and dedup key. | Requeue the existing job, or use another key. |

## Running jobs

These are written to the `error` column of the job, which ends `abandoned`.

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL4001` | Configuration | A worker took a job whose type has no handler in that process, usually because instances run different versions or configurations. | Declare the type on every instance that runs workers, then requeue. |
| `SL4002` | Internal | The job was taken more often than its attempts allow without ever reporting a result. Its worker most likely dies while running it. | Look for crashes or out-of-memory kills during this job, fix them, then requeue. |

## The HTTP source

Recipes are checked when they are loaded, and every problem is reported at once. Messages name the recipe and the field, never a header value or a credential.

| Code | Kind | Cause | Fix |
| --- | --- | --- | --- |
| `SL5001` | Validation | The recipe is not valid JSON, misses `name` or `request`, has a field the format does not know (often a typo), or a value of the wrong type. The message says which. | Correct the JSON; see [recipes.md](recipes.md). |
| `SL5002` | Validation | A required field is empty, such as `name` or `request.url`. | Fill it in. |
| `SL5003` | Validation | `request.url` is not absolute, does not use https, or contains a user name or password. | Use an absolute https URL. Put credentials in headers. Set `allowHttp` only for a local test server. |
| `SL5004` | Validation | A placeholder names a value that does not exist. The message lists the ones that do. | Use `{{window.start}}`, `{{window.end}}` or `{{owner.key}}`. |
| `SL5005` | Validation | A placeholder appears in the scheme, host or port of the URL. A recipe may only call the host it names. | Write the host out. Use the path or the query for values that change. |
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
| `SL5101` | Configuration | The recipe uses `{{window.start}}` or `{{window.end}}`, and the job has no window. | Declare the job type with `Windowed`, or remove the window placeholders. |
| `SL5102` | Validation | A header would contain a line break once its placeholders were filled, usually from an owner key. The request is not sent. | Remove line breaks from the owner key. |
| `SL5103` | Configuration | The response has nothing at the `records` path. | Compare the path with a real response. |
| `SL5104` | Configuration | The `records` path leads to an object or a value, not an array. | Point the path at the list itself. |
| `SL5105` | Throttled | The API answered 429. The job waits for `Retry-After`, cut to `MaxRetryAfter`, or for the queue's throttle delay. | Nothing, unless it keeps happening: then lower `Concurrency` or widen `every`. |
| `SL5106` | Unauthorized | The API answered 401 or 403. The job is abandoned at once: the same credentials will not work on the next try. | Fix the owner's credentials, then requeue. |
| `SL5107` | Unavailable | The API answered 408 or a 5xx status. The job is retried with the queue's growing delay. | Nothing, unless it keeps happening. |
| `SL5108` | Unavailable | The API did not answer within `Timeout`. The job is retried. | Ask for smaller pages, or raise `Timeout`. |
| `SL5109` | Unavailable | The API could not be reached: DNS, connection or TLS failed. The inner exception says which. The job is retried. | Check the host name and the network. |
| `SL5110` | Configuration | The API answered another 4xx, such as 400, 404 or 422. Retrying would get the same answer, so the job is abandoned. | Compare the recipe with the API's documentation, then requeue. |
| `SL5111` | Configuration | The API answered with a redirect. SyncLane follows none, so a recipe only calls the host it names and headers never reach another host. | Put the final URL in the recipe. |
| `SL5112` | Configuration | The response is larger than `MaxResponseBytes`. The job is abandoned. | Ask for smaller pages, or raise `MaxResponseBytes`. |
| `SL5113` | Unavailable | The response is not valid JSON, often because it was cut off. The job is retried. The body is not quoted in the message. | Nothing, unless it keeps happening: then check what the API returns. |
| `SL5114` | Configuration | The next page's URL, from the answer, the `Link` header or the job's cursor, points to another scheme, host or port. It is not called, so the recipe's headers never reach another host. The job is abandoned. | If the API really moves between hosts, write a recipe per host. |
| `SL5115` | Configuration | The job's cursor is not one the recipe's pagination can read, usually because the pagination type changed while the job was halfway. | Clear the job's cursor and requeue it; it starts from the first page. |
| `SL5116` | Configuration | The cursor, next URL or has-more flag in the answer has the wrong JSON type. | Compare the pagination paths with a real response. |
| `SL5117` | Configuration | The answer points to the page that was just read. Following it would loop forever. | Check that the recipe sends the cursor in the parameter the API reads. |
| `SL5118` | Configuration | A job reached `MaxPagesPerJob` pages with page or offset paging. The API may ignore the page parameter. | Check the parameter, cut smaller slices, or raise `MaxPagesPerJob`. |

## Errors that are not on this page

- **Exceptions your handler throws** are stored in the `error` column as they are, type and message, and drive the retry policy. They have no SyncLane code.
- **A `null` argument** throws `ArgumentNullException`. It is a bug in the calling code, not a condition to handle.
- **A database that cannot be reached** throws the `NpgsqlException` that Npgsql raised. Inside a worker it ends the attempt and the job is retried.
- **Cancellation** throws `OperationCanceledException`. It is never reported as a failure.
