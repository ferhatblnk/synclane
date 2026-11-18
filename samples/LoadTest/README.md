# Load test

A bench you can run yourself: many accounts connect at once and their history loads, as after a launch or a long outage. It measures how fast SyncLane drains that, and checks that nothing went wrong on the way.

```bash
samples/LoadTest/run.sh                    # the published scenario, about five minutes
WORKERS=8 OWNERS=2000 samples/LoadTest/run.sh
```

It needs Docker with Compose v2. It builds one image, starts PostgreSQL, a pretend API and the SyncLane instances, prints a report, writes it to `samples/LoadTest/results/`, and removes the stack. It exits with 1 when a correctness check fails.

## The scenario

| | Default | Variable |
| --- | --- | --- |
| Accounts, each an owner in a lane of its own | 1,000 | `OWNERS` |
| History per account | 730 days in 30-day slices: 25 jobs | `HORIZON_DAYS`, `SLICE_DAYS` |
| Pages per job, records per page | 10 of 100 | `PAGES`, `RECORDS` |
| SyncLane instances, slots in each | 4 × 16 | `WORKERS`, `SLOTS` |
| The budget declared to SyncLane | 15 requests a second per account | `BUDGET_LIMIT` |
| The API's answer time | 50 ms ± 20% | `LATENCY_MS` |
| Answers that fail with `503` | 1% | `FAILURE_RATE` |
| The API's real limit, which SyncLane is not told | 30 a second, with a burst of 2 | `HIDDEN_LIMIT`, `HIDDEN_BURST` |
| Accounts whose real limit is below the budget, so they answer `429` | every twentieth, at 8 a second | `HOT_SHARE`, `HOT_LIMIT` |

That is 25,000 jobs and 250,000 pages. Each account can go no faster than 15 pages a second (the budget; the API alone would allow 20), so 64 slots can do at most 960 pages a second. The report says how close the run came.

## Three witnesses

SyncLane's own tables could only say what SyncLane believes it did, so the report puts two independent witnesses beside them:

- **The destination** keeps one row per page in `loadtest.pages`, and counts a write of a page it already has. A missing row is a lost page; a count above one is a page written twice.
- **The pretend API** counts every request it receives. Two requests of one account at the same moment mean two jobs of one lane ran together. The requests an account received in each second, against its budget, show whether the budget held across instances.

## Reading the report

The run passes when all five hold: every job completed and none was abandoned; no page lost; no page written twice; never two requests of one account at once; no account above its budget in any second. A page written twice is allowed by SyncLane's promise (at least once) after a crash; this scenario has none, so it should not happen here.

The numbers are the numbers of the machine the run was on. The report says which PostgreSQL and how many CPUs; say the rest when you share it.

## The files

| File | What it is |
| --- | --- |
| `Program.cs` | One program, three roles: `api`, `worker`, `report` |
| `PretendApi.cs` | The API: slow pages, failures, a hidden limit per account, and its counts |
| `Worker.cs` | A SyncLane instance: the owner source, the handler with its rate budget, and the destination |
| `Report.cs` | Waits for the run to end, measures, and writes the report |
| `docker-compose.yml`, `Dockerfile`, `init.sql`, `run.sh` | The bench |
