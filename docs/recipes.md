# Recipes

A recipe tells `SyncLane.Http` how to fetch one job type over HTTP: the request, how the job's window is written into it, and where the records are in the answer. It replaces a hand-written `IJobHandler` for APIs that fit it.

> **Preview.** Recipes run, page, authenticate and keep to a rate budget, and have synced five public APIs, one per paging style ([samples/LiveCheck](../samples/LiveCheck)). They have not yet met the APIs of a production integration; see [design/phase-1.md](design/phase-1.md).

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
| 429 | The job waits for `Retry-After` (at most `MaxRetryAfter`), or the queue's throttle delay, and continues. With a [rate budget](#rate-budgets), the whole budget pauses for everyone on the key, and the job waits as long as the pause. | `SL5105` |
| A budget's token is further away than `MaxBudgetWait` | The job gives its slot back without using an attempt and runs again when the token is due. Nothing is sent. | `SL4003` |
| 401 to an OAuth 2.0 token kept from an earlier page | The token is renewed once and the page sent again. | |
| 401, 403 | The job is abandoned at once. The same credentials will not work on the next try. | `SL5106` |
| 408, 5xx | Retried with the queue's growing delay. | `SL5107` |
| No answer within `Timeout` | Retried. | `SL5108` |
| DNS, connection, TLS or proxy failure | Retried. The message says which; the exception's own text is not kept. | `SL5109` |
| 3xx | Abandoned. Redirects are not followed. | `SL5111` |
| Other 4xx | Abandoned: retrying gets the same answer. | `SL5110` |
| Larger than `MaxResponseBytes` | Abandoned. | `SL5112` |
| Not valid JSON | Retried. | `SL5113` |
| Nothing at the records path | Retried; some APIs answer 200 with an error now and then. | `SL5103`, `SL5104` |
| No credential for the owner, or one that lacks a value | Abandoned before anything is sent. | `SL5119`, `SL5120` |
| The token endpoint refuses, or answers without a usable token | Abandoned. | `SL5121`, `SL5123` |
| The token endpoint answers 408, 429 or 5xx, times out or cannot be reached | Retried, or waits after 429. | `SL5122` |

An abandoned job waits for you: fix the cause, then `RequeueAsync`. It continues from its cursor.

### Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Timeout` | 100 seconds | How long one request may take, to the last byte of the answer: a page, or a token from a token endpoint. |
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

## Authentication

A recipe names a credential; it never holds one. For every page, SyncLane asks your credential store for the values of the job's owner and adds them as the auth type says.

```jsonc
"auth": { "type": "bearer", "credential": "example-shop" }
```

| `type` | What is sent | Values it needs | Other fields |
| --- | --- | --- | --- |
| `bearer` | `Authorization: Bearer <Token>` | `Token` | |
| `basic` | `Authorization: Basic`, user name and password in UTF-8 | `Username`, and `Password` if there is one | |
| `apiKey` | The key in a header or a query parameter | `Token` | `name` (required); `in`: `header` (default) or `query`; `prefix` before a key in a header, such as `"Token "` |
| `oauth2` | `Authorization: Bearer` with an access token from `tokenUrl` | Client credentials: `ClientId`, `ClientSecret`. Refresh token: `ClientId`, `RefreshToken`, and `ClientSecret` if the client has one | `grant`: `clientCredentials` or `refreshToken` (required); `tokenUrl` (required); `scope`; `clientAuth`: `basic` (default) or `body` |
| `signed` | Whatever your signer adds | Any, including `Values` | `signer` (required) |

`credential` is required for every type. A field that does not apply to the type is an error, as with pagination. Prefer a header for an API key when the API accepts one: a URL is recorded in more places than a header, by proxies and by the API's own access logs.

### The credential store

```csharp
builder.Services.AddSyncLaneHttp(http =>
{
    http.Credentials<ShopCredentials>();
    http.Recipe("orders", File.ReadAllText("recipes/orders.json")).WriteTo<OrderSink>();
});
```

```csharp
public sealed class ShopCredentials(AppDb db, Secrets secrets) : ICredentialStore
{
    public async ValueTask<Credential?> GetAsync(CredentialKey key, CancellationToken cancellationToken)
    {
        var connection = await db.Connections.FindAsync([key.Owner, key.Name], cancellationToken);
        return connection is null ? null : new Credential { Token = secrets.Decrypt(connection.Token) };
    }

    public async ValueTask SaveRefreshTokenAsync(CredentialKey key, string refreshToken, CancellationToken cancellationToken)
    {
        var connection = await db.Connections.FindAsync([key.Owner, key.Name], cancellationToken);
        connection!.RefreshToken = secrets.Encrypt(refreshToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
```

- `key.Owner` is the job's owner; `key.Name` is the recipe's `credential`. Recipes for one account share a name.
- Credentials stay where you keep them, encrypted as you choose. SyncLane stores none.
- `GetAsync` is called once for every page and resolved from the page's scope, so a scoped database context works. Cache inside the store if a lookup is slow or rate limited.
- Return `null` when the owner has no credential, for example after they disconnected: the job is abandoned with `SL5119`. Throw when the store cannot answer: the job is retried.
- A recipe with `auth` and no registered store stops the application at start-up (`SL5020`).

### OAuth 2.0

- The access token is fetched when a page first needs it and kept in memory, one per owner and credential. It is renewed a minute before it expires (a tenth of its lifetime, for tokens that live less than ten minutes). Without `expires_in` it is kept until the API answers 401, and never longer than a day.
- Pages that need a token at the same moment share one request to the token endpoint.
- A 401 to a kept token renews it once and sends the page again. A 401 to a token fetched for that very page abandons the job.
- When the token endpoint issues a new refresh token, `SaveRefreshTokenAsync` is called before the page goes on, with a token that is never cancelled: the old refresh token may already be retired.
- With several instances, two may use the same rotating refresh token at once, and the slower one gets `invalid_grant`. SyncLane then reads the store once more and, if it holds a different refresh token, tries again with it. If the other instance has not saved yet, the job is abandoned with `SL5121` and a requeue fixes it.
- Tokens are never written anywhere, so a restarted process fetches new ones.
- Token requests go through the same `HttpClient`, so they never follow a redirect either.
- Not supported: the authorization code flow itself, the consent screen where the owner grants access, which stays in your application (SyncLane starts from the refresh token it produced); the device flow; client authentication with a signed JWT or mutual TLS. Use a hand-written handler for those.

### Signed requests

For APIs that want a signature, for example a hash of the method, path and time:

```csharp
builder.Services.AddSyncLaneHttp(http =>
{
    http.Credentials<ShopCredentials>();
    http.Signer<ExampleHmacSigner>("example-hmac");
    http.Recipe("orders", File.ReadAllText("recipes/orders.json")).WriteTo<OrderSink>();
});
```

```csharp
public sealed class ExampleHmacSigner(TimeProvider time) : IRequestSigner
{
    public ValueTask SignAsync(HttpRequestMessage request, Credential credential, CancellationToken cancellationToken)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var data = Encoding.UTF8.GetBytes($"{request.Method}\n{request.RequestUri!.PathAndQuery}\n{timestamp}");
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credential.Values["secret"]), data));

        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Signature", signature);
        return ValueTask.CompletedTask;
    }
}
```

The recipe says `"auth": { "type": "signed", "credential": "example-shop", "signer": "example-hmac" }`. The signer sees the complete request: the URL with its paging parameters, the headers and the body. It may add headers and query parameters; changing the method, scheme, host or port abandons the job (`SL5124`), so a signer cannot send a request somewhere the recipe does not name.

### Where a credential never goes

- **Into a recipe.** `Authorization` and `Proxy-Authorization` in `request.headers` are refused (`SL5019`). A recipe can be shared and committed.
- **Into the jobs table.** Many APIs copy a query-string key into their next-page URLs. The key is taken out before the URL becomes the job's cursor, and added again when the page is requested.
- **Into a message, a log or a span.** Messages name the credential and the missing value, such as "has no ClientSecret", never the value. A network error keeps only its kind, not the exception's text, which can hold the URL or a proxy's password. From a token endpoint's refusal, only a standard RFC 6749 code such as `invalid_grant` is quoted.
- **To another host.** No redirects are followed, next-page URLs on another host are refused, and a signer cannot move the request.

A test holds this: every type runs through the engine against an API and a token endpoint that echo every secret they receive in bodies and exception messages, and the test searches every log line, span, metric and database row for them. What it cannot cover is tracing of `HttpClient` itself, if you turn it on: a key in a query string is part of the URL there. One more thing SyncLane does not pretend: .NET cannot wipe a string from memory, so a credential lives in memory as long as the page that uses it.

## Rate budgets

An API's limit belongs to an account. Declare it once in `AddSyncLane`, and name it in every recipe that calls the account:

```csharp
lane.AddBudget("example-shop", limit: 2, per: TimeSpan.FromSeconds(1), burst: 40);
```

```jsonc
"rate": { "budget": "example-shop" }
```

| Field | Required | What it is |
| --- | --- | --- |
| `budget` | yes | The budget's name, as declared with `AddBudget`. A recipe naming an undeclared budget stops the application at start-up (`SL5023`). |
| `key` | no | Whose bucket. Default `{{owner.key}}`: one bucket per owner. Write a fixed text, such as `"shared-account"`, for owners that share one set of credentials and so one limit. `{{owner.key}}` is the only placeholder allowed. |

- Every request to the API waits for a token: the first request of a page, and the one sent again after an OAuth 2.0 token is renewed. Requests to a token endpoint do not draw from the budget.
- A `429` pauses the budget for everyone on the key: for `Retry-After` when the API sent it, otherwise for a backoff that starts at one token's time (at least a second) and doubles while `429`s continue, up to five minutes. After the pause the bucket refills from empty, so requests restart at the steady rate.
- A token further away than `MaxBudgetWait` (30 seconds) postpones the job: it gives its slot back without using an attempt.
- Several recipes can name one budget: an account's limit is usually shared by all its endpoints. An API that limits each operation separately gets one budget per operation.
- **Choosing the numbers.** Read the API's documentation for its algorithm and scope.
  - For a token or leaky bucket, set `limit` per `per` to its rate and `burst` to its bucket size.
  - For a window that resets on the clock, or when nothing is documented, keep `burst` at 1 and the limit a little under the published one: other apps on the same account draw from it too.
- With several instances, each keeps its own buckets for now: give each a share of the limit.

## Fields

| Field | Required | What it is |
| --- | --- | --- |
| `name` | yes | A name for messages and logs. |
| `request.method` | no | `GET` (default) or `POST`. |
| `request.url` | yes | Absolute `https` URL. Placeholders may appear in the path and the query, never in the scheme, host or port. |
| `request.query` | no | Parameters added to the URL. Values are strings and may contain placeholders. |
| `request.headers` | no | Request headers. Values may contain placeholders. `Host`, `Content-Length`, `Transfer-Encoding` and `Connection` cannot be set; `Authorization` and `Proxy-Authorization` are refused, because credentials never go in a recipe. |
| `request.body` | no | A JSON body, for `POST` only. Placeholders are filled in string values. |
| `request.allowHttp` | no | Allow a plain `http` URL, and a plain `http` token URL. For local test servers only. |
| `records` | no | Where the array of records is in the answer: `$` (default) for the root, or `$.data.items`. Property names only. |
| `window.format` | no | How `{{window.start}}` and `{{window.end}}` are written. Default `iso8601`. |
| `pagination` | no | How to get the next page. See [Pagination](#pagination). |
| `auth` | no | How requests are authenticated. See [Authentication](#authentication). |
| `rate` | no | The rate budget the requests draw from. See [Rate budgets](#rate-budgets). |

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
- **Hold a credential.** `https://user:password@host` and an `Authorization` header are refused. A recipe names a credential; the credential store gives its values. See [Authentication](#authentication).
- **Inject into the request.** Placeholder values are percent-encoded in the URL and written as JSON strings in the body, so a value cannot add a parameter or change the shape of the body. A header that would contain a line break after filling is not sent.
- **Put a secret into an error message.** Messages name the recipe and the field, never a header value, a credential, a URL or a response body. A network error keeps only its kind, never the exception's own text.
- **Follow a redirect.** A redirect could send the recipe's headers, keys included, to another host. It abandons the job instead.
- **Log its URLs.** The `HttpClient` loggers that would write each request's URL, owner key and query included, are removed.
- **Decompress with anything but the standard.** Answers in gzip, deflate or brotli are accepted when the server offers them. There is no compression of SyncLane's own.

Every error code is listed in [errors.md](errors.md#the-http-source).
