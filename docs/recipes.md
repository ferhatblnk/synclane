# Recipes

A recipe tells `SyncLane.Http` how to fetch one job type over HTTP: the request, how the job's window is written into it, and where the records are in the answer. It replaces a hand-written `IJobHandler` for APIs that fit it.

> **Preview.** This page describes the recipe format as of phase 1.1. Recipes can be loaded and checked, and the request of a page can be built. Sending it, paging and authentication arrive in the next parts; see [design/phase-1.md](design/phase-1.md).

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
- **Put a secret into an error message.** Messages name the recipe and the field, never a header value or a credential.

Every error code is listed in [errors.md](errors.md#the-http-source).
