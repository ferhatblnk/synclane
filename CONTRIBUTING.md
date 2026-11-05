# Contributing

Thanks for taking the time. Bug reports, questions and pull requests are all welcome. Everyone taking part follows the [code of conduct](CODE_OF_CONDUCT.md).

## Before you start

Read [CLAUDE.md](CLAUDE.md) for the rules and [ARCHITECTURE.md](ARCHITECTURE.md) for the reasons behind them. Some rules are checked by `ConventionTests` and fail the build.

For anything larger than a small fix, open an issue first and describe what you want to change. It saves you from building something that does not fit the project's scope: a small scheduler for sync jobs, with one PostgreSQL table, no lock service and no runtime dependency beyond Npgsql and the `Microsoft.Extensions` abstractions.

Some things that would be welcome are listed under [Ideas](#ideas) below.

## Building and testing

You need the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

The build treats warnings as errors.

About half of the tests need PostgreSQL. They are skipped unless `SYNCLANE_TEST_DB` holds a connection string, and CI always runs them, so a pull request that only passes the unit tests is not finished yet. CI runs them on PostgreSQL 14 to 18. Each test creates a schema of its own and drops it afterwards.

```bash
docker run -d --name synclane-test -e POSTGRES_HOST_AUTH_METHOD=trust -p 5432:5432 postgres:17
SYNCLANE_TEST_DB="Host=localhost;Username=postgres;Database=postgres" dotnet test
```

To watch the whole thing work, run the demo described in the [README](README.md#try-the-demo).

## Pull requests

- Keep a pull request to one change, and say in the description what it does and why.
- Add or update tests for behaviour you change. Planning rules are covered by `JobPlannerTests` and `PlanningTests`, everything about taking and holding jobs by `LeaseTests`, and the page loop by `RunnerTests`; follow the existing ones.
- A change to a SQL statement needs a test that runs it against PostgreSQL.
- If you change the table, add a migration to the end of `Migrations.All`. Never edit one that has been released, and make it safe to run twice (`IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`). Then regenerate `docs/schema.sql` from `SyncLaneSchema.Script()`; a test compares them.
- Update `README.md` and `docs/` when a setting or behaviour changes.
- Match the surrounding code: file-scoped namespaces, one type per file, names that say what a thing is, constants instead of magic values.
- Do not add a runtime dependency to `src/SyncLane` without discussing it first.
- Sign off every commit (see below).

## Checks on a pull request

A pull request is merged when all of these pass:

| Check | What it does |
| --- | --- |
| PostgreSQL 14 to 18 | Builds and runs every test against each supported PostgreSQL. |
| Unit tests on Windows and macOS | Builds and runs the tests that need no database. |
| Packages | Packs the four packages, fails on a dependency with a known vulnerability, and checks that every library the `SyncLane.Cli` tool carries is listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). |
| Signed off (DCO) | Every commit has a `Signed-off-by:` line with its author's email. |
| CodeQL | Looks for security problems in the code. |
| Dependency review | Fails when the change adds a dependency with a known vulnerability or under a license outside the allowed list (permissive licenses such as MIT, Apache-2.0 and BSD). |

Pull requests from forks get no secrets and only read access, so a change cannot reach the release credentials.

## Signing off your commits (DCO)

By signing off a commit you certify the [Developer Certificate of Origin](DCO): that you wrote the change, or otherwise have the right to submit it under the project's license. Nothing is signed or sent anywhere else, and you keep the copyright of your work.

Sign off with `-s`. It adds a line with your name and the email of your commit:

```bash
git commit -s -m "Fix the cursor of the last page"
# Signed-off-by: Your Name <you@example.com>
```

Forgot it? `git rebase --signoff origin/main` signs off every commit of your branch; then push it again with `--force-with-lease`.

## Releasing

Packages are published by [release.yml](.github/workflows/release.yml) when a version tag is pushed. The workflow holds no API key: nuget.org trusts this repository's `release.yml` through trusted publishing.

1. Set `<Version>` in `Directory.Build.props`.
2. Give the version its section in `CHANGELOG.md`, with the date: `## [0.3.0] - 2026-12-01`. The section becomes the release notes.
3. Merge to `main`, then tag that commit and push the tag:

   ```bash
   git tag v0.3.0
   git push origin v0.3.0
   ```

The workflow then:
1. checks that the tag matches the version and that the changelog has its section;
2. runs every test against PostgreSQL;
3. packs `SyncLane`, `SyncLane.Http`, `SyncLane.Cli` and `SyncLane.AspNetCore` with their symbol packages;
4. pushes them to nuget.org;
5. creates a GitHub release with the packages attached.

A version with a hyphen, such as `0.3.0-preview.1`, is marked as a pre-release.

The push and the GitHub release try again a few times when GitHub or nuget.org answers with a server error. If the job still fails, use "Re-run failed jobs": versions already on nuget.org are skipped, and an existing GitHub release gets the packages added.

A version on nuget.org cannot be replaced, only unlisted. If something is wrong after publishing, fix it and release the next version.

## Three rules the planner must keep

They are explained in [docs/planning.md](docs/planning.md). A change that breaks one of them will not be merged, however convenient:

1. No early return and no "already planned" flag in the planner.
2. Coverage does not look at job status.
3. Recurring jobs are deduplicated by period, never by window.

## Ideas

- Enqueueing inside the caller's own transaction.
- A small JSON payload per job for handlers that need more than owner, type and window.

## Never share real data

Do not paste real connection strings, tokens, account identifiers, hostnames or customer data into issues, pull requests or tests. Reduce a problem to a made-up example first. The `error` column in particular holds exception messages, and exception messages are exactly the kind of text that leaks secrets.

## Security problems

Do not report them in a public issue. See [SECURITY.md](SECURITY.md).

## License

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE), and you keep the copyright of what you contributed. "The SyncLane Authors" in the notices are the people in [AUTHORS](AUTHORS) and every contributor in the history; add yourself to `AUTHORS` in your first pull request if you like.
