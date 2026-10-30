# Live check

Syncs public APIs with recipes only, through the real engine, and compares what arrived with what each API says it has. It is the gate of phase 1.5 ([docs/design/phase-1.md](../../docs/design/phase-1.md)): if recipes cannot describe real APIs without custom code, the declarative approach is too weak.

It is run by hand, never in CI: live APIs are slow, rate limited and change without notice.

| Job type | API | Paging | Checked against |
| --- | --- | --- | --- |
| `breweries` | Open Brewery DB, breweries in Oregon | page number | its `meta` total |
| `earthquakes` | USGS earthquake catalogue, yesterday (UTC) | offset from 1, in the job's window | its `count` endpoint |
| `author-feed` | Bluesky public API, the posts of `bsky.app` | cursor in the answer | no total available |
| `pokemon` | PokeAPI | next URL in the answer | its `count` |
| `issues` | GitHub, `dotnet/runtime` issues updated since yesterday | `Link` header | no total available |
| `issues-auth` | The same, with a bearer token | `Link` header | runs only when a token is given |

None of them needs an account. Each run makes about 35 requests, 7 of them to GitHub, which allows 60 an hour without a token.

## Run it

Any PostgreSQL will do. The check creates a schema of its own (`livecheck_<time>`) and drops it at the end.

```bash
docker run --rm -d -p 5432:5432 -e POSTGRES_HOST_AUTH_METHOD=trust --name livecheck-db postgres:17
SYNCLANE_LIVE_DB="Host=localhost;Username=postgres" dotnet run --project samples/LiveCheck
```

To check authentication against a real API too, give it any GitHub token; it needs no scope:

```bash
SYNCLANE_LIVE_GITHUB_TOKEN=<token> SYNCLANE_LIVE_DB="Host=localhost;Username=postgres" dotnet run --project samples/LiveCheck
```

The token is read by `EnvironmentCredentials`, a credential store of four lines. A real application reads each owner's credential from its own encrypted storage.

## Reading the result

```
Recipe                             Paging     Status     Tries  Pages  Records  Distinct  Expected  Verdict
breweries                          page       Completed      1      6      296       296       296  ok
```

- **Records** is what reached the sink; **Distinct** counts records by their full JSON, so a page read twice shows as a difference.
- **Expected** is the total the API reports through another endpoint. A small difference on `earthquakes` can be real: the catalogue revises recent events.
- The exit code is 0 when every row says `ok`.

The APIs, their endpoints and their behaviour are described here as they were on the date of the last recorded run in [docs/design/phase-1.md](../../docs/design/phase-1.md). They are not statements by the providers, and they may have changed.
