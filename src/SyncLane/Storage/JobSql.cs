namespace SyncLane.Storage;

/// <summary>Every statement SyncLane runs, built once for the configured schema.</summary>
internal sealed class JobSql
{
    public const string TableName = "jobs";

    public const string CoverageTableName = "coverage";

    private const string Outstanding = "('pending', 'running', 'failed', 'deferred')";

    private static readonly string[] ColumnNames =
    [
        "id", "queue", "owner", "lane", "type", "mode", "status", "priority", "window_start", "window_end", "dedup_key",
        "cursor", "processed_count", "attempt_count", "error", "next_run_at", "leased_by", "lease_expires_at",
        "created_at", "started_at", "heartbeat_at", "completed_at"
    ];

    public JobSql(string schema)
    {
        Schema = schema;
        var table = $"{schema}.{TableName}";
        var columns = string.Join(", ", ColumnNames);
        var leasedColumns = string.Join(", ", ColumnNames.Select(column => $"j.{column}"));
        const string held = "id = @id AND status = 'running' AND leased_by = @worker";

        var coverage = $"{schema}.{CoverageTableName}";

        // Planning facts are fixed when a job is created, so the statement that creates jobs also keeps the summary the
        // planner reads. Proposals that already exist are counted too: a job an older instance inserted during a rolling
        // deploy reaches the summary the next time it is proposed.
        string Summarize() =>
            $"""
            existing AS (
                SELECT j.owner, j.type, j.mode, j.window_start, j.window_end, j.created_at
                FROM {table} AS j
                JOIN proposed AS p ON j.owner = p.owner AND j.type = p.type AND j.dedup_key = p.dedup_key
                WHERE j.queue = @queue AND p.mode IN ('initial', 'recurring')
            ),
            summarized AS (
                INSERT INTO {coverage} AS c
                    (queue, owner, type, has_initial, initial_from, covered_to, last_planned_at, last_recurring_at)
                SELECT @queue, s.owner, s.type,
                       bool_or(s.mode = 'initial'),
                       min(s.window_start) FILTER (WHERE s.mode = 'initial'),
                       max(s.window_end),
                       max(s.created_at),
                       max(s.created_at) FILTER (WHERE s.mode = 'recurring')
                FROM (SELECT owner, type, mode, window_start, window_end, created_at FROM inserted
                      UNION ALL
                      SELECT owner, type, mode, window_start, window_end, created_at FROM existing) AS s
                WHERE s.mode IN ('initial', 'recurring')
                GROUP BY s.owner, s.type
                ON CONFLICT (queue, owner, type) DO UPDATE
                SET has_initial = c.has_initial OR excluded.has_initial,
                    initial_from = LEAST(c.initial_from, excluded.initial_from),
                    covered_to = GREATEST(c.covered_to, excluded.covered_to),
                    last_planned_at = GREATEST(c.last_planned_at, excluded.last_planned_at),
                    last_recurring_at = GREATEST(c.last_recurring_at, excluded.last_recurring_at)
            )
            """;

        // A recurring job that was given up is replaced by the next one, whose window reaches back over it. The delete
        // only takes rows still abandoned, so a job someone requeued in the meantime survives.
        string Insert(string result) =>
            $"""
            WITH proposed AS (
                SELECT *
                FROM unnest(@owners, @lanes, @types, @modes, @priorities, @window_starts, @window_ends, @dedup_keys)
                    AS t(owner, lane, type, mode, priority, window_start, window_end, dedup_key)
            ),
            replaced AS (
                DELETE FROM {table}
                WHERE id = ANY(@replaced) AND queue = @queue AND mode = 'recurring' AND status = 'abandoned'
            ),
            inserted AS (
                INSERT INTO {table}
                    (queue, owner, lane, type, mode, priority, window_start, window_end, dedup_key, next_run_at, created_at, updated_at)
                SELECT @queue, owner, lane, type, mode, priority, window_start, window_end, dedup_key, @now, @now, @now
                FROM proposed
                ON CONFLICT (queue, owner, type, dedup_key) DO NOTHING
                RETURNING id, owner, type, mode, window_start, window_end, created_at
            ),
            {Summarize()}
            {result}
            """;

        Enqueue = Insert("SELECT count(*)::int FROM inserted");
        EnqueueOne = Insert("SELECT id FROM inserted");

        MarkLoaded =
            $"""
            WITH proposed AS (
                SELECT t.*, 'initial'::text AS mode
                FROM unnest(@owners, @lanes, @types, @priorities, @window_starts, @window_ends, @dedup_keys)
                    AS t(owner, lane, type, priority, window_start, window_end, dedup_key)
            ),
            inserted AS (
                INSERT INTO {table}
                    (queue, owner, lane, type, mode, status, priority, window_start, window_end, dedup_key,
                     next_run_at, created_at, completed_at, updated_at)
                SELECT @queue, owner, lane, type, mode, 'completed', priority, window_start, window_end, dedup_key,
                       @now, @now, @now, @now
                FROM proposed
                ON CONFLICT (queue, owner, type, dedup_key) DO NOTHING
                RETURNING id, owner, type, mode, window_start, window_end, created_at
            ),
            {Summarize()}
            SELECT count(*)::int FROM inserted
            """;

        Lease =
            $"""
            UPDATE {table} AS j
            SET status = 'running',
                leased_by = @worker,
                lease_expires_at = @lease_expires_at,
                attempt_count = j.attempt_count + 1,
                started_at = COALESCE(j.started_at, @now),
                heartbeat_at = @now,
                updated_at = @now
            WHERE j.id = (
                SELECT c.id
                FROM {table} AS c
                WHERE c.queue = @queue
                  AND c.next_run_at <= @now
                  AND (c.status IN ('pending', 'failed')
                       OR (c.status = 'running' AND c.lease_expires_at <= @now)
                       OR (c.status = 'deferred' AND NOT EXISTS (
                               SELECT 1
                               FROM {table} AS w
                               WHERE w.queue = c.queue AND w.lane = c.lane AND w.id <> c.id
                                 AND w.status IN ('pending', 'running', 'failed'))))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM {table} AS r
                      WHERE r.queue = c.queue AND r.lane = c.lane AND r.id <> c.id
                        AND r.status = 'running')
                ORDER BY (c.status = 'deferred'), c.priority, c.window_start DESC NULLS LAST, c.next_run_at, c.id
                LIMIT 1
                FOR UPDATE OF c SKIP LOCKED)
            RETURNING {leasedColumns}
            """;

        // A saved page proves the attempt works: the count starts again at this one, so a long job that fails now and
        // then, with progress in between, is never abandoned for failures spread over its whole life.
        Checkpoint =
            $"""
            UPDATE {table}
            SET cursor = @cursor,
                processed_count = processed_count + @processed,
                attempt_count = 1,
                heartbeat_at = @now,
                lease_expires_at = @lease_expires_at,
                updated_at = @now
            WHERE {held}
            """;

        Heartbeat =
            $"""
            UPDATE {table}
            SET heartbeat_at = @now, lease_expires_at = @lease_expires_at, updated_at = @now
            WHERE {held}
            """;

        Complete =
            $"""
            UPDATE {table}
            SET status = 'completed',
                completed_at = @now,
                error = NULL,
                cursor = NULL,
                leased_by = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE {held}
            """;

        Settle =
            $"""
            UPDATE {table}
            SET status = @status,
                error = @error,
                next_run_at = @next_run_at,
                completed_at = CASE WHEN @status = 'abandoned' THEN @now END,
                leased_by = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE {held}
            """;

        Release =
            $"""
            UPDATE {table}
            SET status = 'pending',
                next_run_at = @now,
                attempt_count = GREATEST(attempt_count - 1, 0),
                leased_by = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE {held}
            """;

        Postpone =
            $"""
            UPDATE {table}
            SET status = 'pending',
                next_run_at = @next_run_at,
                attempt_count = GREATEST(attempt_count - 1, 0),
                error = @error,
                leased_by = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE {held}
            """;

        Requeue =
            $"""
            UPDATE {table}
            SET status = 'pending',
                attempt_count = 0,
                next_run_at = @now,
                error = NULL,
                completed_at = NULL,
                leased_by = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE queue = @queue
              AND status IN ('failed', 'deferred', 'abandoned')
              AND (@owner::text IS NULL OR owner = @owner)
            """;

        Cancel =
            $"""
            UPDATE {table}
            SET status = 'abandoned', error = @reason, completed_at = @now, updated_at = @now
            WHERE queue = @queue AND owner = @owner AND status IN ('pending', 'failed', 'deferred')
            """;

        // The summary answers what was planned; only the open rows, which retention never deletes, say what is still
        // under way. Those are few, and the jobs_open index finds them per owner and type.
        Coverage =
            $"""
            SELECT c.owner,
                   c.type,
                   c.has_initial,
                   c.initial_from,
                   c.covered_to,
                   coalesce(o.any_outstanding, false),
                   coalesce(o.recurring_outstanding, false),
                   c.last_planned_at,
                   c.last_recurring_at,
                   o.abandoned_from,
                   o.abandoned
            FROM {coverage} AS c
            LEFT JOIN LATERAL (
                SELECT bool_or(j.status IN {Outstanding}) AS any_outstanding,
                       bool_or(j.mode = 'recurring' AND j.status IN {Outstanding}) AS recurring_outstanding,
                       min(j.window_start) FILTER (WHERE j.mode = 'recurring' AND j.status = 'abandoned') AS abandoned_from,
                       array_agg(j.id) FILTER (WHERE j.mode = 'recurring' AND j.status = 'abandoned') AS abandoned
                FROM {table} AS j
                WHERE j.queue = c.queue AND j.owner = c.owner AND j.type = c.type
                  AND j.status <> 'completed' AND j.mode IN ('initial', 'recurring')
            ) AS o ON true
            WHERE c.queue = @queue AND c.owner = ANY(@owners)
            """;

        ForgetOwner =
            $"""
            WITH forgotten AS (
                DELETE FROM {coverage} WHERE queue = @queue AND owner = @owner
            )
            DELETE FROM {table} WHERE queue = @queue AND owner = @owner
            """;

        // Initial jobs stay: they are bounded by the horizon, progress counts them, and they record what history was
        // loaded. Recurring and manual jobs are what grows with time.
        DeleteCompleted =
            $"""
            DELETE FROM {table}
            WHERE id IN (
                SELECT id
                FROM {table}
                WHERE queue = ANY(@queues) AND status = 'completed' AND mode <> 'initial' AND completed_at < @before
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            """;

        Progress =
            $"""
            SELECT count(*)::int,
                   (count(*) FILTER (WHERE status = 'completed'))::int,
                   (count(*) FILTER (WHERE status IN {Outstanding}))::int,
                   (count(*) FILTER (WHERE status = 'abandoned'))::int
            FROM {table}
            WHERE queue = @queue AND owner = @owner AND mode = 'initial'
            """;

        const string filters =
            """
            (@queue::text IS NULL OR queue = @queue)
              AND (@owner::text IS NULL OR owner = @owner)
              AND (@status::text IS NULL OR status = @status)
            """;

        List =
            $"""
            SELECT {columns}
            FROM {table}
            WHERE {filters}
              AND (@before::bigint IS NULL OR id < @before)
            ORDER BY id DESC
            LIMIT @limit
            """;

        Count = $"SELECT count(*) FROM {table} WHERE {filters}";

        Find = $"SELECT {columns} FROM {table} WHERE id = @id";

        LockSchema = "SELECT pg_advisory_xact_lock(hashtext('synclane.schema'))";
        AppliedMigrations = $"SELECT version FROM {schema}.{Migrations.TableName}";
        HasMigrationTable = "SELECT to_regclass(@name) IS NOT NULL";
    }

    public string Schema { get; }

    /// <summary>Inserts planned jobs, replaces abandoned recurring ones and updates the summary. Returns how many were created.</summary>
    public string Enqueue { get; }

    /// <summary>The same as <see cref="Enqueue"/> for one job. Returns its id, or nothing when it exists.</summary>
    public string EnqueueOne { get; }

    /// <summary>Records history loaded elsewhere as completed initial jobs, and updates the summary.</summary>
    public string MarkLoaded { get; }

    public string Lease { get; }

    public string Checkpoint { get; }

    public string Heartbeat { get; }

    public string Complete { get; }

    public string Settle { get; }

    public string Release { get; }

    /// <summary>Hands a job back to run later, without counting the attempt: it only had to wait.</summary>
    public string Postpone { get; }

    public string Requeue { get; }

    public string Cancel { get; }

    public string Coverage { get; }

    public string ForgetOwner { get; }

    public string DeleteCompleted { get; }

    public string Progress { get; }

    public string List { get; }

    public string Count { get; }

    public string Find { get; }

    public string LockSchema { get; }

    public string AppliedMigrations { get; }

    public string HasMigrationTable { get; }
}
