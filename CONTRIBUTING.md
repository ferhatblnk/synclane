# Contributing

Thanks for taking the time. Bug reports, questions and pull requests are all welcome.

## Before you start

For anything larger than a small fix, open an issue first and describe what you want to change. It saves you from building something that does not fit the project's scope: a small scheduler for sync jobs, with one PostgreSQL table, no lock service and no runtime dependency beyond Npgsql and the `Microsoft.Extensions` abstractions.

Some things that would be welcome are listed under [Ideas](#ideas) below.

## Building and testing

You need the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

The build treats warnings as errors.

About half of the tests need PostgreSQL. They are skipped unless `SYNCLANE_TEST_DB` holds a connection string, and CI always runs them, so a pull request that only passes the unit tests is not finished yet. Development happened against PostgreSQL 18 and CI uses 17. Each test creates a schema of its own and drops it afterwards.

```bash
docker run -d --name synclane-test -e POSTGRES_HOST_AUTH_METHOD=trust -p 5432:5432 postgres:17
SYNCLANE_TEST_DB="Host=localhost;Username=postgres;Database=postgres" dotnet test
```

To watch the whole thing work, run the demo described in the [README](README.md#try-the-demo).

## Pull requests

- Keep a pull request to one change, and say in the description what it does and why.
- Add or update tests for behaviour you change. Planning rules are covered by `JobPlannerTests` and `PlanningTests`, everything about taking and holding jobs by `LeaseTests`, and the page loop by `RunnerTests`; follow the existing ones.
- A change to a SQL statement needs a test that runs it against PostgreSQL.
- If you change the table, change `JobSql.Ddl` and `docs/schema.sql` together. A test compares them.
- Update `README.md` and `docs/` when a setting or behaviour changes.
- Match the surrounding code: file-scoped namespaces, one type per file, names that say what a thing is, constants instead of magic values.
- Do not add a runtime dependency to `src/SyncLane` without discussing it first.

## Three rules the planner must keep

They are explained in [docs/planning.md](docs/planning.md). A change that breaks one of them will not be merged, however convenient:

1. No early return and no "already planned" flag in the planner.
2. Coverage does not look at job status.
3. Recurring jobs are deduplicated by period, never by window.

## Ideas

- `LISTEN`/`NOTIFY` so an idle slot wakes up when a job is enqueued instead of polling.
- Enqueueing inside the caller's own transaction.
- A small JSON payload per job for handlers that need more than owner, type and window.
- Metrics through `System.Diagnostics.Metrics`: jobs taken, pages, failures, time waiting.
- A retention helper that implements the deletes described in [docs/operations.md](docs/operations.md).
- A package on NuGet, with a release workflow.

## Never share real data

Do not paste real connection strings, tokens, account identifiers, hostnames or customer data into issues, pull requests or tests. Reduce a problem to a made-up example first. The `error` column in particular holds exception messages, and exception messages are exactly the kind of text that leaks secrets.

## Security problems

Do not report them in a public issue. See [SECURITY.md](SECURITY.md).

## License

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
