# Recipes

A recipe tells `SyncLane.Http` how to fetch one job type over HTTP: the request, how the job's window is written into it, and where the records are in the answer. It replaces a hand-written `IJobHandler` for APIs that fit it.

> **Preview.** As of phase 1.3 recipes run and page. Authentication beyond fixed headers arrives in the next part; see [design/phase-1.md](design/phase-1.md).

## A recipe

```jsonc
{
  "$schema": "https://example.com/recipe.v1.json",   // optional; for editors only
  "name": "example-shop/orders",
  "request": {
    "method": "GET",
    "url": "https://api.example-shop.com/v2/shops/{{owner.key}}/orders",
    "query": {
      "updated_after": "{{window.start}}",
      "updated_before": "{{window.end}}",
      "size": "200"
    },
    "headers": { "Accept": "application/json" }
  },
  "records": "$.content",
  "window": { "format": "iso8601" }
}
```

The platform and endpoint above are made up; the format is real. Comments and trailing commas are allowed. A field the format does not know is an error, so a typo is caught instead of silently ignored.

Load it and see every problem at once:

```csharp
var result = HttpRecipe.Parse(File.ReadAllText("recipes/orders.json"));
if (!result.Succeeded)
{
    foreach (var error in result.Errors)
        Console.WriteLine(error);   // SL5004: Recipe 'example-shop/orders': request.query.from uses {{window.begin}}, ...
    return;
}
```

The same recipe can be built in C#, as `new HttpRecipe { Name = ..., Request = new HttpRecipeRequest { ... } }`, and checked with `recipe.Validate()`.

## Running a recipe

Declare the job type with `HttpRecipeHandler`, and register its recipe and where its records go:

```csharp
builder.Services.AddSyncLane(lane =>
{
    lane.ConnectionString = builder.Configuration.GetConnectionString("Postgres")!;
    lane.AddQueue("shops", queue =>
    {
        queue.Owners<ConnectedShops>();
        queue.Windowed<HttpRecipeHandler>("orders", horizon: TimeSpan.FromDays(365), slice: TimeSpan.FromDays(7), every: TimeSpan.FromMinutes(15));
    });
});

builder.Services.AddSyncLaneHttp(http =>
{
    http.Recipe("orders", File.ReadAllText("recipes/orders.json")).WriteTo<OrderSink>();
});
```

A sink receives each page's records:

```csharp
public sealed class OrderSink(AppDb db) : IRecordSink
{
    public async Task WriteAsync(RecordPage page, CancellationToken cancellationToken)
    {
        foreach (var record in page.Records.EnumerateArray())
            db.Orders.Upsert(page.Job.Owner, record);   // a page can run twice: write with upserts

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

`page.Records` is only valid during the call; clone what you keep. For a quick start, `.WriteTo((page, cancellationToken) => ...)` takes a delegate instead of a class.

Every problem is reported before anything runs. `AddSyncLaneHttp` throws one `SyncLaneException` holding every error of every recipe and setting. When the application starts, it compares recipes with the declared job types: a job type declared with `HttpRecipeHandler` and no recipe (`SL5014`), or a recipe for a type no queue declares (`SL5015`), stops it before any job runs.

### How answers become outcomes

| Answer | Outcome | Code |
| --- | --- | --- |
| 2xx | Records go to the sink; the job completes. | |
| 429 | The job waits for `Retry-After` (at most `MaxRetryAfter`), or the queue's throttle delay, and continues. | `SL5105` |
| 401, 403 | The job is abandoned at once. The same credentials will not work on the next try. | `SL5106` |
| 408, 5xx | Retried with the queue's growing delay. | `SL5107` |
| No answer within `Timeout` | Retried. | `SL5108` |
| DNS, connection or TLS failure | Retried. | `SL5109` |
| 3xx | Abandoned. Redirects are not followed. | `SL5111` |
| Other 4xx | Abandoned: retrying gets the same answer. | `SL5110` |
| Larger than `MaxResponseBytes` | Abandoned. | `SL5112` |
| Not valid JSON | Retried. | `SL5113` |
| Nothing at the records path | Retried; some APIs answer 200 with an error now and then. | `SL5103`, `SL5104` |

An abandoned job waits for you: fix the cause, then `RequeueAsync`. It continues from its cursor.

### Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Timeout` | 100 seconds | How long one request may take, to the last byte of the answer. |
| `MaxResponseBytes` | 16 MiB | The largest answer read. |
| `MaxRetryAfter` | 1 hour | The longest wait taken from a `Retry-After` header. |
| `MaxPagesPerJob` | 1,000 | The most pages one job reads with `page` or `offset` paging. |
| `UserAgent` | `SyncLane.Http/<version>` | Sent when a recipe sets no `User-Agent`. |

The requests go through the named `HttpClient` `HttpSource.ClientName`. Add handlers to it with `services.AddHttpClient(HttpSource.ClientName)`, for a proxy for example. Do not add retry handlers: the engine already retries the job from its cursor.

## Pagination

Add `pagination` when one request does not return everything. The job's cursor holds where paging stands, so a job that stops for any reason continues from the page it reached, not from the first.

| Type | For APIs that | Fields | Last page |
| --- | --- | --- | --- |
| `page` | take a page number: `?page=3` | `param` (required), `start` (default 1), `size`, `sizeParam` | With `size`: the first page shorter than it. Without: the first empty page. |
| `offset` | take a record offset: `?offset=200` | `param` (required), `size` (required), `start` (default 0), `sizeParam` | The first page shorter than `size`. |
| `cursor` | return a token to send back: `?cursor=abc` | `param` (required); `from` (a path in the answer) or `fromLastRecord` (a property of the last record); `hasMore`, `size`, `sizeParam` | No token, or `hasMore` is `false`. |
| `nextUrl` | return the next page's full URL in the body | `from` (required) | No URL. |
| `linkHeader` | return it in a `Link` header with `rel="next"` | none | No `next` link. |

```jsonc
// A page number and a page size:
"pagination": { "type": "page", "param": "page", "size": 100, "sizeParam": "per_page" }

// A cursor taken from the last record, stopping on a has-more flag:
"pagination": { "type": "cursor", "param": "starting_after", "fromLastRecord": "id", "hasMore": "$.has_more" }

// The next page's URL in the body:
"pagination": { "type": "nextUrl", "from": "$.links.next" }
```

A field that does not apply to the type is an error (`SL5017`), as is a paging parameter that is also set in `request.query`.

Paging is guarded against the usual ways it goes wrong:

- **A host the recipe does not name.** A next URL, from the body, the `Link` header or a cursor someone edited in the table, must have the recipe's scheme, host and port. Otherwise it is not called and the job is abandoned (`SL5114`). Relative URLs are read against the request that returned them.
- **A loop.** An answer that points to the page just read stops the job (`SL5117`). With `page` and `offset`, a job stops after `MaxPagesPerJob` pages (default 1,000; `SL5118`), which catches an API that ignores the page parameter.
- **A change of recipe halfway.** A cursor the pagination cannot read abandons the job instead of guessing (`SL5115`).

The next cursor is worked out before the sink runs, so a page whose next step cannot be followed is not written.

With `nextUrl` and `linkHeader`, pages after the first are called exactly as the API wrote the URL; the recipe's `query` is not added again. Headers and body are sent with every page.

Not supported yet: a page number, offset or cursor that the API expects in the body or in a header rather than in the query, as GraphQL APIs often do. Such APIs need a hand-written handler for now.

## Fields

| Field | Required | What it is |
| --- | --- | --- |
| `name` | yes | A name for messages and logs. |
| `request.method` | no | `GET` (default) or `POST`. |
| `request.url` | yes | Absolute `https` URL. Placeholders may appear in the path and the query, never in the scheme, host or port. |
| `request.query` | no | Parameters added to the URL. Values are strings and may contain placeholders. |
| `request.headers` | no | Request headers. Values may contain placeholders. `Host`, `Content-Length`, `Transfer-Encoding` and `Connection` cannot be set. |
| `request.body` | no | A JSON body, for `POST` only. Placeholders are filled in string values. |
| `request.allowHttp` | no | Allow a plain `http` URL. For local test servers only. |
| `records` | no | Where the array of records is in the answer: `$` (default) for the root, or `$.data.items`. Property names only. |
| `window.format` | no | How `{{window.start}}` and `{{window.end}}` are written. Default `iso8601`. |
| `pagination` | no | How to get the next page. See [Pagination](#pagination). |

## Placeholders

| Placeholder | Value |
| --- | --- |
| `{{window.start}}` | Start of the job's window, in `window.format`. |
| `{{window.end}}` | End of the job's window, in `window.format`. |
| `{{owner.key}}` | The job's owner key. |

A placeholder is a name and nothing more: no expressions, no filters, no nesting. Any other name is an error when the recipe is loaded.

A job without a window cannot run a recipe that uses `{{window.start}}` or `{{window.end}}` (`SL5101`). Declare such job types with `Windowed`.

## Window formats

All formats are in UTC.

| Format | Example |
| --- | --- |
| `iso8601` | `2026-10-01T00:00:00Z` |
| `unix` | `1790812800` |
| `unixMilliseconds` | `1790812800000` |
| `date` | `2026-10-01`. Only for windows that start and end at midnight. |

## What a recipe can never do

These are deliberate limits, checked when the recipe is loaded or the request is built:

- **Call a host it does not name.** The scheme, host and port are fixed text. A placeholder cannot change where the request goes.
- **Carry credentials in the URL.** `https://user:password@host` is refused. Credentials go in headers, and with phase 1.4 in a credential store.
- **Inject into the request.** Placeholder values are percent-encoded in the URL and written as JSON strings in the body, so a value cannot add a parameter or change the shape of the body. A header that would contain a line break after filling is not sent.
- **Put a secret into an error message.** Messages name the recipe and the field, never a header value, a credential, a URL or a response body.
- **Follow a redirect.** A redirect could send the recipe's headers, keys included, to another host. It abandons the job instead.
- **Log its URLs.** The `HttpClient` loggers that would write each request's URL, owner key and query included, are removed.
- **Decompress with anything but the standard.** Answers in gzip, deflate or brotli are accepted when the server offers them. There is no compression of SyncLane's own.

Every error code is listed in [errors.md](errors.md#the-http-source).
