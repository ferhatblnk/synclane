namespace SyncLane.Storage;

/// <summary>Every statement SyncLane runs, built once for the configured schema.</summary>
internal sealed class JobSql
{
    public const string TableName = "jobs";

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

        Enqueue =
            $"""
            INSERT INTO {table}
                (queue, owner, lane, type, mode, priority, window_start, window_end, dedup_key, next_run_at, created_at, updated_at)
            SELECT @queue, t.owner, t.lane, t.type, t.mode, t.priority, t.window_start, t.window_end, t.dedup_key, @now, @now, @now
            FROM unnest(@owners, @lanes, @types, @modes, @priorities, @window_starts, @window_ends, @dedup_keys)
                AS t(owner, lane, type, mode, priority, window_start, window_end, dedup_key)
            ON CONFLICT (queue, owner, type, dedup_key) DO NOTHING
            RETURNING id
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

        Checkpoint =
            $"""
            UPDATE {table}
            SET cursor = @cursor,
                processed_count = processed_count + @processed,
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

        Coverage =
            $"""
            SELECT owner,
                   type,
                   bool_or(mode = 'initial'),
                   min(window_start) FILTER (WHERE mode = 'initial'),
                   max(window_end),
                   bool_or(status IN {Outstanding}),
                   bool_or(mode = 'recurring' AND status IN {Outstanding}),
                   max(created_at),
                   max(created_at) FILTER (WHERE mode = 'recurring')
            FROM {table}
            WHERE queue = @queue AND owner = ANY(@owners) AND mode IN ('initial', 'recurring')
            GROUP BY owner, type
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

    public string Enqueue { get; }

    public string Lease { get; }

    public string Checkpoint { get; }

    public string Heartbeat { get; }

    public string Complete { get; }

    public string Settle { get; }

    public string Release { get; }

    public string Requeue { get; }

    public string Cancel { get; }

    public string Coverage { get; }

    public string Progress { get; }

    public string List { get; }

    public string Count { get; }

    public string Find { get; }

    public string LockSchema { get; }

    public string AppliedMigrations { get; }

    public string HasMigrationTable { get; }
}
