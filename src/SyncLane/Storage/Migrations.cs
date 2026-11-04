namespace SyncLane.Storage;

/// <summary>The schema of SyncLane as an ordered list of steps, and the table that records which steps a database has.</summary>
internal static class Migrations
{
    public const string TableName = "migrations";

    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "jobs table", JobsTable),
        new(2, "rate buckets", RateBucketsTable),
        new(3, "rate bucket sequence", RateBucketSequence),
        new(4, "learned rates", LearnedRates),
        new(5, "coverage summary", CoverageSummary),
    ];

    public static int Latest => All[^1].Version;

    /// <summary>The schema and the table that records applied steps. Both are created before any step runs.</summary>
    public static string HistoryTable(string schema) =>
        $"""
        CREATE SCHEMA IF NOT EXISTS {schema};

        CREATE TABLE IF NOT EXISTS {schema}.{TableName} (
            version    integer     PRIMARY KEY,
            name       text        NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        );
        """;

    /// <summary>Marks a step as applied. Names are constants without quotes, so they can be inlined.</summary>
    public static string Record(string schema, Migration migration) =>
        $"INSERT INTO {schema}.{TableName} (version, name) VALUES ({migration.Version}, '{migration.Name}') ON CONFLICT (version) DO NOTHING;";

    /// <summary>
    /// Version 1: the jobs table as SyncLane 0.1 created it. Every statement checks for existence first, so a table
    /// created before migrations existed is adopted as it is.
    /// </summary>
    private static string JobsTable(string schema) =>
        $"""
        CREATE TABLE IF NOT EXISTS {schema}.{JobSql.TableName} (
            id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            queue            text        NOT NULL,
            owner            text        NOT NULL,
            lane             text        NOT NULL,
            type             text        NOT NULL,
            mode             text        NOT NULL,
            status           text        NOT NULL DEFAULT 'pending',
            priority         integer     NOT NULL DEFAULT 0,
            window_start     timestamptz,
            window_end       timestamptz,
            dedup_key        text,
            cursor           text,
            processed_count  bigint      NOT NULL DEFAULT 0,
            attempt_count    integer     NOT NULL DEFAULT 0,
            error            text,
            next_run_at      timestamptz NOT NULL,
            leased_by        text,
            lease_expires_at timestamptz,
            created_at       timestamptz NOT NULL,
            started_at       timestamptz,
            heartbeat_at     timestamptz,
            completed_at     timestamptz,
            updated_at       timestamptz NOT NULL,
            CONSTRAINT jobs_mode_check CHECK (mode IN ('initial', 'recurring', 'manual')),
            CONSTRAINT jobs_status_check CHECK (status IN ('pending', 'running', 'completed', 'failed', 'deferred', 'abandoned'))
        );

        CREATE UNIQUE INDEX IF NOT EXISTS jobs_dedup
            ON {schema}.{JobSql.TableName} (queue, owner, type, dedup_key);

        CREATE UNIQUE INDEX IF NOT EXISTS jobs_one_running_per_lane
            ON {schema}.{JobSql.TableName} (queue, lane) WHERE status = 'running';

        CREATE INDEX IF NOT EXISTS jobs_due
            ON {schema}.{JobSql.TableName} (queue, next_run_at) WHERE status IN ('pending', 'running', 'failed', 'deferred');
        """;

    /// <summary>
    /// Version 2: one token bucket per rate budget and key, shared by every instance. <c>refilled_at</c> lies in the
    /// future while the bucket is paused; <c>generation</c> counts pauses, so a request knows whether one came after it.
    /// </summary>
    private static string RateBucketsTable(string schema) =>
        $"""
        CREATE TABLE IF NOT EXISTS {schema}.{BudgetSql.TableName} (
            budget       text             NOT NULL,
            key          text             NOT NULL,
            tokens       double precision NOT NULL,
            refilled_at  timestamptz      NOT NULL,
            generation   bigint           NOT NULL DEFAULT 0,
            streak       integer          NOT NULL DEFAULT 0,
            throttled_at timestamptz,
            last_pause   interval         NOT NULL DEFAULT interval '0',
            updated_at   timestamptz      NOT NULL,
            PRIMARY KEY (budget, key)
        );
        """;

    /// <summary>
    /// Version 3: how many tokens a bucket has given, ever. A token remembers the count it was given at, so a reading of
    /// what the API has left can subtract the requests that took a token after it and have not been counted yet.
    /// </summary>
    private static string RateBucketSequence(string schema) =>
        $"ALTER TABLE {schema}.{BudgetSql.TableName} ADD COLUMN IF NOT EXISTS taken bigint NOT NULL DEFAULT 0;";

    /// <summary>
    /// Version 4: the rate a bucket has learned, the rate that last caused a throttle, and how many tokens it had given
    /// when it last paused. A row without a rate starts at its budget's starting rate.
    /// </summary>
    private static string LearnedRates(string schema) =>
        $"""
        ALTER TABLE {schema}.{BudgetSql.TableName}
            ADD COLUMN IF NOT EXISTS rate double precision,
            ADD COLUMN IF NOT EXISTS rate_max double precision,
            ADD COLUMN IF NOT EXISTS taken_at_pause bigint NOT NULL DEFAULT 0;
        """;

    /// <summary>
    /// Version 5: what planning knows per queue, owner and type, so that it reads one row instead of every job ever
    /// created and old jobs can be deleted. It is filled from the existing jobs once. An older instance that keeps
    /// running ignores it, and the jobs it creates reach it the next time a newer planner proposes them. Two indexes
    /// come with it: open jobs per owner and type for the planner, and completed jobs by age for retention.
    /// </summary>
    private static string CoverageSummary(string schema) =>
        $"""
        CREATE TABLE IF NOT EXISTS {schema}.{JobSql.CoverageTableName} (
            queue             text        NOT NULL,
            owner             text        NOT NULL,
            type              text        NOT NULL,
            has_initial       boolean     NOT NULL DEFAULT false,
            initial_from      timestamptz,
            covered_to        timestamptz,
            last_planned_at   timestamptz,
            last_recurring_at timestamptz,
            PRIMARY KEY (queue, owner, type)
        );

        INSERT INTO {schema}.{JobSql.CoverageTableName}
            (queue, owner, type, has_initial, initial_from, covered_to, last_planned_at, last_recurring_at)
        SELECT queue, owner, type,
               bool_or(mode = 'initial'),
               min(window_start) FILTER (WHERE mode = 'initial'),
               max(window_end),
               max(created_at),
               max(created_at) FILTER (WHERE mode = 'recurring')
        FROM {schema}.{JobSql.TableName}
        WHERE mode IN ('initial', 'recurring')
        GROUP BY queue, owner, type
        ON CONFLICT (queue, owner, type) DO NOTHING;

        CREATE INDEX IF NOT EXISTS jobs_open
            ON {schema}.{JobSql.TableName} (queue, owner, type) WHERE status <> 'completed';

        CREATE INDEX IF NOT EXISTS jobs_finished
            ON {schema}.{JobSql.TableName} (queue, completed_at) WHERE status = 'completed' AND mode <> 'initial';
        """;
}
