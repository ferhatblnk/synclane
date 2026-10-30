namespace SyncLane.Storage;

/// <summary>The schema of SyncLane as an ordered list of steps, and the table that records which steps a database has.</summary>
internal static class Migrations
{
    public const string TableName = "migrations";

    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "jobs table", JobsTable),
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
}
