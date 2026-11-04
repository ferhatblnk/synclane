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
        new(6, "instances", InstancesTable),
        new(7, "monitoring", Monitoring),
        new(8, "change notifications", ChangeNotifications),
        new(9, "operations", Operations),
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

    /// <summary>
    /// Version 6: one row per running process, written at start-up and every half minute: its application, the queues it
    /// runs and the budgets it declares. Start-up reads it to keep two applications off one queue. Older instances do not
    /// write it, and nothing they do reads it.
    /// </summary>
    private static string InstancesTable(string schema) =>
        $"""
        CREATE TABLE IF NOT EXISTS {schema}.{InstanceSql.TableName} (
            id           text        PRIMARY KEY,
            application  text        NOT NULL,
            host         text        NOT NULL,
            version      text        NOT NULL,
            queues       text[]      NOT NULL,
            budgets      jsonb       NOT NULL,
            started_at   timestamptz NOT NULL,
            heartbeat_at timestamptz NOT NULL
        );
        """;

    /// <summary>
    /// Version 7: what the monitor reads without scanning jobs. <c>coverage</c> learns how far each owner's completed
    /// data reaches; <c>job_stats</c> counts completed jobs, their records, failed attempts and abandoned jobs per queue,
    /// type and hour, and keeps them after clean-up deletes the jobs. Both are filled from the existing jobs once. An
    /// older instance does not update them, so its work shows up late or not at all until it is upgraded. An index on
    /// initial jobs lets progress be counted without reading an owner's recurring history.
    /// </summary>
    private static string Monitoring(string schema) =>
        $"""
        ALTER TABLE {schema}.{JobSql.CoverageTableName}
            ADD COLUMN IF NOT EXISTS completed_to timestamptz,
            ADD COLUMN IF NOT EXISTS completed_at timestamptz;

        UPDATE {schema}.{JobSql.CoverageTableName} AS c
        SET completed_to = d.completed_to, completed_at = d.completed_at
        FROM (SELECT queue, owner, type, max(window_end) AS completed_to, max(completed_at) AS completed_at
              FROM {schema}.{JobSql.TableName}
              WHERE status = 'completed' AND mode IN ('initial', 'recurring')
              GROUP BY queue, owner, type) AS d
        WHERE c.queue = d.queue AND c.owner = d.owner AND c.type = d.type AND c.completed_at IS NULL;

        CREATE TABLE IF NOT EXISTS {schema}.{JobSql.StatsTableName} (
            queue     text        NOT NULL,
            type      text        NOT NULL,
            hour      timestamptz NOT NULL,
            completed integer     NOT NULL DEFAULT 0,
            records   bigint      NOT NULL DEFAULT 0,
            failures  integer     NOT NULL DEFAULT 0,
            abandoned integer     NOT NULL DEFAULT 0,
            PRIMARY KEY (queue, type, hour)
        );

        CREATE INDEX IF NOT EXISTS job_stats_hour ON {schema}.{JobSql.StatsTableName} (hour);

        CREATE INDEX IF NOT EXISTS jobs_initial
            ON {schema}.{JobSql.TableName} (queue, owner, type) INCLUDE (status) WHERE mode = 'initial';

        INSERT INTO {schema}.{JobSql.StatsTableName} (queue, type, hour, completed, records, failures, abandoned)
        SELECT queue, type, date_trunc('hour', completed_at, 'UTC'),
               count(*) FILTER (WHERE status = 'completed'),
               coalesce(sum(processed_count) FILTER (WHERE status = 'completed'), 0),
               count(*) FILTER (WHERE status = 'abandoned'),
               count(*) FILTER (WHERE status = 'abandoned')
        FROM {schema}.{JobSql.TableName}
        WHERE status IN ('completed', 'abandoned') AND completed_at IS NOT NULL
        GROUP BY queue, type, date_trunc('hour', completed_at, 'UTC')
        ON CONFLICT (queue, type, hour) DO NOTHING;
        """;

    /// <summary>
    /// Version 8: every job that is created or changes status is announced on the schema's notification channel, after
    /// its transaction commits, to every connection that listens: the monitor's live view and idle workers. A trigger does
    /// it, so no writer can forget, older instances included. Saving a page or a heartbeat does not change the status and
    /// announces nothing. Fields are cut short so a long owner key can never make the announcement fail the write.
    /// </summary>
    private static string ChangeNotifications(string schema) =>
        $"""
        CREATE OR REPLACE FUNCTION {schema}.announce_job() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM pg_notify('{ChangeChannel.For(schema)}', json_build_object(
                'id', NEW.id,
                'queue', left(NEW.queue, 200),
                'owner', left(NEW.owner, 500),
                'type', left(NEW.type, 200),
                'status', NEW.status,
                'at', NEW.updated_at)::text);
            RETURN NULL;
        END
        $$;

        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'jobs_announce_insert' AND tgrelid = '{schema}.{JobSql.TableName}'::regclass) THEN
                CREATE TRIGGER jobs_announce_insert
                    AFTER INSERT ON {schema}.{JobSql.TableName}
                    FOR EACH ROW EXECUTE FUNCTION {schema}.announce_job();
            END IF;

            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'jobs_announce_status' AND tgrelid = '{schema}.{JobSql.TableName}'::regclass) THEN
                CREATE TRIGGER jobs_announce_status
                    AFTER UPDATE OF status ON {schema}.{JobSql.TableName}
                    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
                    EXECUTE FUNCTION {schema}.announce_job();
            END IF;
        END
        $$;
        """;

    /// <summary>
    /// Version 9: what operators and APIs change while SyncLane runs.
    /// <list type="bullet">
    /// <item><c>pauses</c>: pauses an operator sets on a queue or an owner, which workers obey.</item>
    /// <item><c>actions</c>: a log of every operator action, with who did it and why, kept for 400 days.</item>
    /// <item><c>budget_limits</c>: temporary limits an operator sets below a budget's ceiling.</item>
    /// <item><c>rate_buckets.reported_rate</c>: the limit the API last reported in a header, which becomes the
    /// budget's ceiling in place of the declared one.</item>
    /// </list>
    /// An older instance reads none of it: it keeps working a paused queue and pacing at the declared limit until it is
    /// upgraded.
    /// </summary>
    private static string Operations(string schema) =>
        $"""
        CREATE TABLE IF NOT EXISTS {schema}.{JobSql.PausesTableName} (
            queue     text        NOT NULL,
            owner     text        NOT NULL DEFAULT '',
            paused_by text        NOT NULL,
            reason    text        NOT NULL,
            paused_at timestamptz NOT NULL,
            until     timestamptz,
            PRIMARY KEY (queue, owner)
        );

        CREATE TABLE IF NOT EXISTS {schema}.{JobSql.ActionsTableName} (
            id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            at      timestamptz NOT NULL,
            by      text        NOT NULL,
            action  text        NOT NULL,
            queue   text,
            owner   text,
            target  text,
            reason  text,
            changed integer     NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS actions_queue ON {schema}.{JobSql.ActionsTableName} (queue, id);

        CREATE INDEX IF NOT EXISTS actions_at ON {schema}.{JobSql.ActionsTableName} (at);

        CREATE TABLE IF NOT EXISTS {schema}.{BudgetSql.LimitsTableName} (
            budget  text             NOT NULL,
            key     text             NOT NULL DEFAULT '',
            rate    double precision NOT NULL,
            until   timestamptz,
            set_by  text             NOT NULL,
            reason  text             NOT NULL,
            set_at  timestamptz      NOT NULL,
            PRIMARY KEY (budget, key)
        );

        ALTER TABLE {schema}.{BudgetSql.TableName}
            ADD COLUMN IF NOT EXISTS reported_rate double precision,
            ADD COLUMN IF NOT EXISTS reported_at timestamptz;
        """;
}
