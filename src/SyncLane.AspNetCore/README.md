# SyncLane.AspNetCore

HTTP endpoints for SyncLane's monitor, in one call. They appear in your application's OpenAPI (Swagger) document under the tag `SyncLane`.

```csharp
builder.Services.AddSyncLane(options => { /* ... */ });

app.MapSyncLane("/synclane").RequireAuthorization("ops");
```

| Method | Path | What it answers |
| --- | --- | --- |
| GET | `/overview` | One line per queue: open jobs by state, owners, instances, the last day |
| GET | `/freshness?queue=&owner=&search=&limit=&cursor=` | Owners ranked by how old their completed data is, with the job that explains it; `search` finds owners by name or key |
| GET | `/problems?queue=` | Failing and abandoned jobs grouped by error code |
| GET | `/budgets?budget=&limit=&cursor=` | Rate budgets: learned rate, pause, last refusal |
| GET | `/instances` | Every process of every application on the schema: alive, stopped (with when) or gone |
| GET | `/volume?from=&to=&queue=&type=&unit=hour` | Completed jobs, records, failures per hour or day |
| GET | `/jobs?queue=&owner=&status=&type=&search=&limit=&cursor=` | Jobs, newest first, with each owner's name; `search` finds owners by name or key |
| GET | `/stream?queue=&owner=` | Server-sent events, one `job` event per created job or status change |
| GET | `/pauses` | The pauses in force: who, why, until when |
| GET | `/budget-limits` | The budget limits in force: who, why, until when |
| GET | `/actions?queue=&owner=&limit=&cursor=` | What operators did, newest first, with who and why |
| POST | `/pause?queue=&owner=&reason=&until=` | Emergency stop of a queue or one owner: running jobs are cut and handed back |
| POST | `/resume?queue=&owner=` | Lift a pause |
| POST | `/budgets/limit?budget=&key=&limit=&per=&reason=&until=` | Lower a rate budget, or one key of it, for a while (`per` as `00:01:00`) |
| POST | `/budgets/clear-limit?budget=&key=` | Lift a budget limit |
| POST | `/jobs/{id}/requeue`, `/jobs/{id}/restart` | Repair one job |
| POST | `/requeue-by-code?queue=&code=` | Requeue every failing job with one error code |
| POST | `/prioritize?queue=&owner=` | Move an owner's waiting jobs ahead of the others |

Safe by default:
- Until the group requires authorization, the endpoints answer only requests from the same machine. A request that came through a proxy (`X-Forwarded-For` and the like) does not count as local.
- Repairs need the header `X-SyncLane-Action`, which a page on another site cannot send. `MapSyncLane(configure: o => o.ReadOnly = true)` leaves them out.
- Answers are never cached and never hold a job's cursor, which can contain the other side's token.
- Each pause, budget limit and repair is written to the actions log with the signed-in user who asked for it.

The stream needs `LISTEN`, which a pooler in transaction mode cannot carry: point SyncLane's connection string at the server.

More: [the monitor](https://github.com/ferhatblnk/synclane#watching-it-run), [error codes](https://github.com/ferhatblnk/synclane/blob/main/docs/errors.md).
