namespace SyncLane.Storage;

/// <summary>Every statement SyncLane runs, built once for the configured schema.</summary>
internal sealed class JobSql
{
    public const string TableName = "jobs";

    public const string CoverageTableName = "coverage";

    public const string StatsTableName = "job_stats";

    public const string PausesTableName = "pauses";

    public const string ActionsTableName = "actions";

    public const string OwnersTableName = "owners";

    /// <summary>
    /// A pause in force over the job aliased <paramref name="job"/>: of its whole queue (owner '') or of its owner, and not
    /// expired. Served by the pauses primary key, on a table of a handful of rows.
    /// </summary>
    public static string PausedOver(string schema, string job) =>
        $"""
        EXISTS (SELECT 1 FROM {schema}.{PausesTableName} AS p
                WHERE p.queue = {job}.queue AND (p.owner = '' OR p.owner = {job}.owner)
                  AND (p.until IS NULL OR p.until > @now))
        """;

    private const string Outstanding = "('pending', 'running', 'failed', 'deferred')";

    private static readonly string[] ColumnNames =
    [
        "id", "queue", "owner", "lane", "type", "mode", "status", "priority", "window_start", "window_end", "dedup_key",
        "cursor", "processed_count", "attempt_count", "error", "next_run_at", "leased_by", "lease_expires_at",
        "created_at", "started_at", "heartbeat_at", "completed_at", "requested_by"
    ];

    public JobSql(string schema)
    {
        Schema = schema;
        var table = $"{schema}.{TableName}";
        var leasedColumns = string.Join(", ", ColumnNames.Select(column => $"j.{column}"));
        const string held = "id = @id AND status = 'running' AND leased_by = @worker";

        var coverage = $"{schema}.{CoverageTableName}";
        var stats = $"{schema}.{StatsTableName}";
        var owners = $"{schema}.{OwnersTableName}";

        // The name travels with every job read, the one a worker takes included: one primary-key lookup per job, never
        // per page.
        var ownerName =
            $"(SELECT o.name FROM {owners} AS o WHERE o.queue = j.queue AND o.owner = j.owner), " +
            $"(SELECT o.owner_values::text FROM {owners} AS o WHERE o.queue = j.queue AND o.owner = j.owner)";

        // Every owner gets a row, named or not, so search reads this small table and never the jobs. A name or values
        // replace different ones; missing ones keep what is stored, and unchanged ones write nothing.
        string Remember(string keys, string names, string values) =>
            $"""
            INSERT INTO {owners} AS o (queue, owner, name, owner_values, updated_at)
            SELECT @queue, t.owner, t.name, t.owner_values::jsonb, @now
            FROM unnest({keys}, {names}, {values}) AS t(owner, name, owner_values)
            ON CONFLICT (queue, owner) DO UPDATE
            SET name = coalesce(excluded.name, o.name),
                owner_values = coalesce(excluded.owner_values, o.owner_values),
                updated_at = excluded.updated_at
            WHERE (excluded.name IS NOT NULL AND o.name IS DISTINCT FROM excluded.name)
               OR (excluded.owner_values IS NOT NULL AND o.owner_values IS DISTINCT FROM excluded.owner_values)
            """;

        // Planning facts are fixed when a job is created, so the statement that creates jobs also keeps the summary the
        // planner reads. Proposals that already exist are counted too: a job an older instance inserted during a rolling
        // deploy reaches the summary the next time it is proposed.
        string Summarize() =>
            $"""
            existing AS (
                SELECT j.owner, j.type, j.mode, j.status, j.window_start, j.window_end, j.created_at, j.completed_at
                FROM {table} AS j
                JOIN proposed AS p ON j.owner = p.owner AND j.type = p.type AND j.dedup_key = p.dedup_key
                WHERE j.queue = @queue AND p.mode IN ('initial', 'recurring')
            ),
            summarized AS (
                INSERT INTO {coverage} AS c
                    (queue, owner, type, has_initial, initial_from, covered_to, last_planned_at, last_recurring_at,
                     completed_to, completed_at)
                SELECT @queue, s.owner, s.type,
                       bool_or(s.mode = 'initial'),
                       min(s.window_start) FILTER (WHERE s.mode = 'initial'),
                       max(s.window_end),
                       max(s.created_at),
                       max(s.created_at) FILTER (WHERE s.mode = 'recurring'),
                       max(s.window_end) FILTER (WHERE s.status = 'completed'),
                       max(s.completed_at) FILTER (WHERE s.status = 'completed')
                FROM (SELECT owner, type, mode, status, window_start, window_end, created_at, completed_at FROM inserted
                      UNION ALL
                      SELECT owner, type, mode, status, window_start, window_end, created_at, completed_at FROM existing) AS s
                WHERE s.mode IN ('initial', 'recurring')
                GROUP BY s.owner, s.type
                ON CONFLICT (queue, owner, type) DO UPDATE
                SET has_initial = c.has_initial OR excluded.has_initial,
                    initial_from = LEAST(c.initial_from, excluded.initial_from),
                    covered_to = GREATEST(c.covered_to, excluded.covered_to),
                    last_planned_at = GREATEST(c.last_planned_at, excluded.last_planned_at),
                    last_recurring_at = GREATEST(c.last_recurring_at, excluded.last_recurring_at),
                    completed_to = GREATEST(c.completed_to, excluded.completed_to),
                    completed_at = GREATEST(c.completed_at, excluded.completed_at)
            )
            """;

        // A recurring job that was given up is replaced by the next one, whose window reaches back over it. The delete
        // only takes rows still abandoned, so a job someone requeued in the meantime survives.
        string Insert(string result, string? remember = null) =>
            $"""
            WITH {(remember is null ? "" : $"remembered AS ({remember}),")}
            proposed AS (
                SELECT *
                FROM unnest(@owners, @lanes, @types, @modes, @priorities, @window_starts, @window_ends, @dedup_keys, @requested_bys)
                    AS t(owner, lane, type, mode, priority, window_start, window_end, dedup_key, requested_by)
            ),
            replaced AS (
                DELETE FROM {table}
                WHERE id = ANY(@replaced) AND queue = @queue AND mode = 'recurring' AND status = 'abandoned'
            ),
            inserted AS (
                INSERT INTO {table}
                    (queue, owner, lane, type, mode, priority, window_start, window_end, dedup_key, requested_by, next_run_at, created_at, updated_at)
                SELECT @queue, owner, lane, type, mode, priority, window_start, window_end, dedup_key, requested_by, @now, @now, @now
                FROM proposed
                ON CONFLICT (queue, owner, type, dedup_key) DO NOTHING
                RETURNING id, owner, type, mode, status, window_start, window_end, created_at, completed_at
            ),
            {Summarize()}
            {result}
            """;

        RememberOwners = Remember("@owner_keys", "@owner_names", "@owner_values");
        Enqueue = Insert("SELECT count(*)::int FROM inserted");
        EnqueueOne = Insert("SELECT id FROM inserted", Remember("ARRAY[@owner_key]", "ARRAY[@owner_name]", "ARRAY[@owner_values]"));

        MarkLoaded =
            $"""
            WITH proposed AS (
                SELECT t.*, 'initial'::text AS mode
                FROM unnest(@owners, @lanes, @types, @priorities, @window_starts, @window_ends, @dedup_keys)
                    AS t(owner, lane, type, priority, window_start, window_end, dedup_key)
            ),
            remembered AS (
                INSERT INTO {owners} (queue, owner, updated_at)
                SELECT DISTINCT @queue, owner, @now::timestamptz FROM proposed
                ON CONFLICT (queue, owner) DO NOTHING
            ),
            inserted AS (
                INSERT INTO {table}
                    (queue, owner, lane, type, mode, status, priority, window_start, window_end, dedup_key,
                     next_run_at, created_at, completed_at, updated_at)
                SELECT @queue, owner, lane, type, mode, 'completed', priority, window_start, window_end, dedup_key,
                       @now, @now, @now, @now
                FROM proposed
                ON CONFLICT (queue, owner, type, dedup_key) DO NOTHING
                RETURNING id, owner, type, mode, status, window_start, window_end, created_at, completed_at
            ),
            {Summarize()}
            SELECT count(*)::int FROM inserted
            """;

        // An ordered queue takes a lane's jobs strictly in the order they were created: a job waits while an older one
        // of its lane is unfinished, failed and abandoned ones included, until it completes or an operator skips it.
        // Its deferred job is then tried again when due, because the jobs it would wait for are waiting for it.
        string LeaseStatement(bool ordered)
        {
            var deferredWaits = ordered
                ? ""
                : $"""
                   AND NOT EXISTS (
                               SELECT 1
                               FROM {table} AS w
                               WHERE w.queue = c.queue AND w.lane = c.lane AND w.id <> c.id
                                 AND w.status IN ('pending', 'running', 'failed'))
                  """;
            // Only the head of each lane can run: its oldest unfinished job. The heads are found by skipping through
            // jobs_lane_order one lane at a time, so the cost grows with the lanes, not with the jobs waiting in them:
            // on 100,000 waiting jobs in 1,000 lanes, 6 ms, against 295 ms for checking every candidate's elders.
            var inOrder = ordered
                ? $"""
                  AND c.id IN (
                      WITH RECURSIVE heads AS (
                          (SELECT h.lane, h.id FROM {table} AS h
                           WHERE h.queue = @queue AND h.status <> 'completed'
                           ORDER BY h.lane, h.id
                           LIMIT 1)
                          UNION ALL
                          SELECT n.lane, n.id
                          FROM heads
                          CROSS JOIN LATERAL (
                              SELECT h.lane, h.id FROM {table} AS h
                              WHERE h.queue = @queue AND h.status <> 'completed' AND h.lane > heads.lane
                              ORDER BY h.lane, h.id
                              LIMIT 1) AS n)
                      SELECT id FROM heads)
                  """
                : "";

            return $"""
                UPDATE {table} AS j
                SET status = 'running',
                    leased_by = @worker,
                    lease_expires_at = @lease_expires_at,
                    attempt_count = j.attempt_count + 1,
                    started_at = COALESCE(j.started_at, @now),
                    heartbeat_at = @now,
                    updated_at = @now
                WHERE j.id = (
                    -- Pauses are read once per statement, not once per candidate: the paused owners of the queue become a
                    -- small array each row is compared with, and a paused queue becomes a one-time filter, so a paused queue
                    -- with many due jobs costs nothing to poll.
                    SELECT c.id
                    FROM {table} AS c
                    WHERE c.queue = @queue
                      AND c.next_run_at <= @now
                      AND (c.status IN ('pending', 'failed')
                           OR (c.status = 'running' AND c.lease_expires_at <= @now)
                           OR (c.status = 'deferred'{deferredWaits}))
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {table} AS r
                          WHERE r.queue = c.queue AND r.lane = c.lane AND r.id <> c.id
                            AND r.status = 'running')
                      {inOrder}
                      AND c.owner <> ALL (ARRAY(
                          SELECT p.owner FROM {schema}.{PausesTableName} AS p
                          WHERE p.queue = @queue AND p.owner <> '' AND (p.until IS NULL OR p.until > @now)))
                      AND NOT EXISTS (
                          SELECT 1 FROM {schema}.{PausesTableName} AS q
                          WHERE q.queue = @queue AND q.owner = '' AND (q.until IS NULL OR q.until > @now))
                    ORDER BY (c.status = 'deferred'), c.priority, c.window_start DESC NULLS LAST, c.next_run_at, c.id
                    LIMIT 1
                    FOR UPDATE OF c SKIP LOCKED)
                RETURNING {leasedColumns}, {ownerName}
                """;
        }

        Lease = LeaseStatement(ordered: false);
        LeaseOrdered = LeaseStatement(ordered: true);

        // A saved page proves the attempt works: the count starts again at this one, so a long job that fails now and
        // then, with progress in between, is never abandoned for failures spread over its whole life.
        // Both say, in the same round trip, whether a pause now covers the job: the way a worker that missed the pause's
        // announcement still stops, at its next page or heartbeat.
        Checkpoint =
            $"""
            UPDATE {table} AS j
            SET cursor = @cursor,
                processed_count = j.processed_count + @processed,
                attempt_count = 1,
                heartbeat_at = @now,
                lease_expires_at = @lease_expires_at,
                updated_at = @now
            WHERE {held}
            RETURNING {PausedOver(schema, "j")}
            """;

        Heartbeat =
            $"""
            UPDATE {table} AS j
            SET heartbeat_at = @now, lease_expires_at = @lease_expires_at, updated_at = @now
            WHERE {held}
            RETURNING {PausedOver(schema, "j")}
            """;

        // Finishing a job also records how far its owner's data now reaches and counts it in its hour, in the same
        // statement: the monitor reads both without scanning jobs, and the counts outlive the rows clean-up deletes. A
        // manual job counts only when its window starts within what is complete, so it never hides a gap, and never past
        // now.
        Complete =
            $"""
            WITH done AS (
                UPDATE {table}
                SET status = 'completed',
                    completed_at = @now,
                    error = NULL,
                    cursor = NULL,
                    leased_by = NULL,
                    lease_expires_at = NULL,
                    updated_at = @now
                WHERE {held}
                RETURNING queue, owner, type, mode, window_start, window_end, processed_count
            ),
            noted AS (
                UPDATE {coverage} AS c
                SET completed_to = GREATEST(c.completed_to, CASE WHEN d.mode = 'manual' THEN LEAST(d.window_end, @now) ELSE d.window_end END),
                    completed_at = @now
                FROM done AS d
                WHERE c.queue = d.queue AND c.owner = d.owner AND c.type = d.type
                  AND (d.mode IN ('initial', 'recurring') OR (d.mode = 'manual' AND d.window_start <= c.completed_to))
            ),
            counted AS (
                INSERT INTO {stats} AS s (queue, type, hour, completed, records, failures, abandoned)
                SELECT queue, type, date_trunc('hour', @now::timestamptz, 'UTC'), 1, processed_count, 0, 0
                FROM done
                ON CONFLICT (queue, type, hour) DO UPDATE
                SET completed = s.completed + 1, records = s.records + excluded.records
            )
            SELECT count(*)::int FROM done
            """;

        Settle =
            $"""
            WITH settled AS (
                UPDATE {table}
                SET status = @status,
                    error = @error,
                    next_run_at = @next_run_at,
                    completed_at = CASE WHEN @status = 'abandoned' THEN @now END,
                    leased_by = NULL,
                    lease_expires_at = NULL,
                    updated_at = @now
                WHERE {held}
                RETURNING queue, type, status
            ),
            counted AS (
                INSERT INTO {stats} AS s (queue, type, hour, completed, records, failures, abandoned)
                SELECT queue, type, date_trunc('hour', @now::timestamptz, 'UTC'), 0, 0, 1, (status = 'abandoned')::int
                FROM settled
                ON CONFLICT (queue, type, hour) DO UPDATE
                SET failures = s.failures + 1, abandoned = s.abandoned + excluded.abandoned
            )
            SELECT count(*)::int FROM settled
            """;

        DeleteOldStats =
            $"""
            DELETE FROM {stats} WHERE hour < @before;
            DELETE FROM {schema}.{ActionsTableName} WHERE at < @before;
            DELETE FROM {schema}.{PausesTableName} WHERE until < @now;
            DELETE FROM {schema}.{BudgetSql.LimitsTableName} WHERE until < @now;
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
            ),
            unnamed AS (
                DELETE FROM {owners} WHERE queue = @queue AND owner = @owner
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
            (@queue::text IS NULL OR j.queue = @queue)
              AND (@owner::text IS NULL OR j.owner = @owner)
              AND (@status::text IS NULL OR j.status = @status)
              AND (@type::text IS NULL OR j.type = @type)
            """;

        var found =
            $"""
            SELECT o.queue, o.owner, o.name, o.owner_values FROM {owners} AS o
            WHERE (@queue::text IS NULL OR o.queue = @queue)
              AND (o.owner ILIKE @search OR o.name ILIKE @search)
            """;

        List =
            $"""
            SELECT {leasedColumns}, {ownerName}
            FROM {table} AS j
            WHERE {filters}
              AND (@before::bigint IS NULL OR j.id < @before)
            ORDER BY j.id DESC
            LIMIT @limit
            """;

        // Owners first, from their small table, then each one's newest jobs through jobs_owner. Filtering the jobs by a
        // search instead walked the whole table backwards when few or none matched: 190 ms on a million jobs, against
        // under a millisecond this way and 18 ms when every owner matches.
        ListFound =
            $"""
            SELECT {leasedColumns}, o.name, o.owner_values::text
            FROM ({found}) AS o
            CROSS JOIN LATERAL (
                SELECT *
                FROM {table} AS j
                WHERE j.queue = o.queue AND j.owner = o.owner AND {filters}
                  AND (@before::bigint IS NULL OR j.id < @before)
                ORDER BY j.id DESC
                LIMIT @limit
            ) AS j
            ORDER BY j.id DESC
            LIMIT @limit
            """;

        Count =
            $"""
            SELECT count(*) FROM {table} AS j
            WHERE {filters}
              AND (@search::text IS NULL OR (j.queue, j.owner) IN (SELECT f.queue, f.owner FROM ({found}) AS f))
            """;

        Find = $"SELECT {leasedColumns}, {ownerName} FROM {table} AS j WHERE j.id = @id";

        LockSchema = "SELECT pg_advisory_xact_lock(hashtext('synclane.schema'))";
        AppliedMigrations = $"SELECT version FROM {schema}.{Migrations.TableName}";
        HasMigrationTable = "SELECT to_regclass(@name) IS NOT NULL";
    }

    public string Schema { get; }

    /// <summary>Adds the owners of a planning round and the names they were given. Runs with the coverage read.</summary>
    public string RememberOwners { get; }

    /// <summary>Inserts planned jobs, replaces abandoned recurring ones and updates the summary. Returns how many were created.</summary>
    public string Enqueue { get; }

    /// <summary>The same as <see cref="Enqueue"/> for one job. Returns its id, or nothing when it exists.</summary>
    public string EnqueueOne { get; }

    /// <summary>Records history loaded elsewhere as completed initial jobs, and updates the summary.</summary>
    public string MarkLoaded { get; }

    public string Lease { get; }

    /// <summary>The lease of an ordered queue: a lane's jobs in the order they were created.</summary>
    public string LeaseOrdered { get; }

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

    public string DeleteOldStats { get; }

    public string Progress { get; }

    public string List { get; }

    /// <summary>The same as <see cref="List"/> for the owners whose key or name matches <c>@search</c>.</summary>
    public string ListFound { get; }

    public string Count { get; }

    public string Find { get; }

    public string LockSchema { get; }

    public string AppliedMigrations { get; }

    public string HasMigrationTable { get; }
}
