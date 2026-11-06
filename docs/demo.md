# The demo

The demo syncs four pretend shop accounts. The pretend shop answers slowly, sometimes returns 429 or 503, and one page of one account is broken on purpose.

```bash
docker compose up --build
```

Open <http://localhost:8080>.

<p align="center">
  <img src="docs/images/demo.png" width="820" alt="The demo page about fifteen seconds after start: four accounts with their progress, three jobs running in three lanes, five jobs waiting for a retry with their cursors and errors, and the remaining slices of history pending.">
</p>

Things to watch for:

- **Lanes.** `acme-us` and `acme-eu` share the lane `acme` and take turns. `globex` and `initech` run beside them.
- **Newest first.** Each account's orders arrive from the most recent month backwards.
- **Retries.** Jobs that hit a 429 or 503 turn `failed`, wait, and continue from the cursor they had reached.
- **Giving up.** One slice of `globex` fails on its second page every time. After five attempts it is `deferred`, the rest of the lane finishes, it gets its last attempt and ends `abandoned` with cursor `50`.
- **Requeue.** Press *Requeue stopped jobs*. The broken page has healed by then, and the job completes from cursor `50`, not from the start.
- **Restart.** Run `docker compose restart demo` during the first minute. The jobs that were running are `pending` again with their cursors, and continue after the restart.

The same information without the page:

```bash
docker compose exec postgres psql -U postgres -d synclane \
  -c "select id, owner, type, status, cursor, processed_count, attempt_count, error from synclane.jobs order by id"
```

<details>
<summary>Without Docker</summary>

You need the .NET 10 SDK and a PostgreSQL database the demo may create two schemas in (`synclane` and `demo`):

```bash
ConnectionStrings__Postgres="Host=localhost;Username=postgres;Database=synclane" \
  ASPNETCORE_URLS=http://localhost:8080 dotnet run --project samples/Demo
```

</details>
