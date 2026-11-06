namespace SyncLane.Storage;

/// <summary>
/// The monitor's statements. They read the summary tables and the open jobs, never the history, so their cost does not
/// grow with time: <c>coverage</c> and <c>job_stats</c> are small, and open jobs are found through <c>jobs_open</c>.
/// </summary>
internal sealed class MonitorSql
{
    /// <summary>The sort key of an owner that has never completed anything: before every real moment.</summary>
    public static readonly DateTimeOffset Never = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public MonitorSql(string schema)
    {
        var jobs = $"{schema}.{JobSql.TableName}";
        var coverage = $"{schema}.{JobSql.CoverageTableName}";
        var stats = $"{schema}.{JobSql.StatsTableName}";
        var instances = $"{schema}.{InstanceSql.TableName}";
        var pauses = $"{schema}.{JobSql.PausesTableName}";
        var actions = $"{schema}.{JobSql.ActionsTableName}";
        var owners = $"{schema}.{JobSql.OwnersTableName}";
        Channel = ChangeChannel.For(schema);

        Overview =
            $"""
            WITH open AS (
                SELECT queue,
                       count(*) FILTER (WHERE status = 'pending' AND next_run_at <= @now) AS due,
                       count(*) FILTER (WHERE status = 'pending' AND next_run_at > @now) AS postponed,
                       count(*) FILTER (WHERE status = 'running') AS running,
                       count(*) FILTER (WHERE status = 'failed') AS retrying,
                       count(*) FILTER (WHERE status = 'deferred') AS deferred,
                       count(*) FILTER (WHERE status = 'abandoned') AS abandoned
                FROM {jobs}
                WHERE status <> 'completed'
                GROUP BY queue
            ),
            owners AS (
                SELECT queue, count(DISTINCT owner) AS owners FROM {coverage} GROUP BY queue
            ),
            day AS (
                SELECT queue, sum(completed) AS completed, sum(records) AS records, sum(failures) AS failures
                FROM {stats}
                WHERE hour >= @since
                GROUP BY queue
            ),
            live AS (
                SELECT q AS queue, count(*) AS instances
                FROM {instances}, unnest(queues) AS q
                WHERE heartbeat_at > @alive_since AND stopped_at IS NULL
                GROUP BY q
            ),
            paused AS (
                SELECT queue, paused_by, reason, paused_at, until
                FROM {pauses}
                WHERE owner = '' AND (until IS NULL OR until > @now)
            )
            SELECT queue,
                   coalesce(owners, 0)::int, coalesce(instances, 0)::int,
                   coalesce(due, 0)::int, coalesce(postponed, 0)::int, coalesce(running, 0)::int,
                   coalesce(retrying, 0)::int, coalesce(deferred, 0)::int, coalesce(abandoned, 0)::int,
                   coalesce(completed, 0)::int, coalesce(records, 0)::bigint, coalesce(failures, 0)::int,
                   paused_by, reason, paused_at, until
            FROM open
            FULL JOIN owners USING (queue)
            FULL JOIN day USING (queue)
            FULL JOIN live USING (queue)
            FULL JOIN paused USING (queue)
            ORDER BY queue
            """;

        // Coverage is sorted first and only the rows of the page look up their open job and their history.
        Freshness =
            $"""
            SELECT c.queue, c.owner, c.type, c.completed_to, c.completed_at,
                   o.status, {CodeOf("o.error")}, o.error, o.next_run_at,
                   coalesce(h.total, 0), coalesce(h.completed, 0),
                   c.sort_key,
                   {JobSql.PausedOver(schema, "c")},
                   (SELECT n.name FROM {owners} AS n WHERE n.queue = c.queue AND n.owner = c.owner)
            FROM (
                SELECT queue, owner, type, completed_to, completed_at, coalesce(completed_to, completed_at, @never) AS sort_key
                FROM {coverage} AS s
                WHERE (@queue::text IS NULL OR s.queue = @queue)
                  AND (@owner::text IS NULL OR s.owner = @owner)
                  AND (@search::text IS NULL OR (s.queue, s.owner) IN (
                          SELECT n.queue, n.owner FROM {owners} AS n
                          WHERE (@queue::text IS NULL OR n.queue = @queue)
                            AND (n.owner ILIKE @search OR n.name ILIKE @search)))
            ) AS c
            LEFT JOIN LATERAL (
                SELECT j.status, j.error, j.next_run_at
                FROM {jobs} AS j
                WHERE j.queue = c.queue AND j.owner = c.owner AND j.type = c.type AND j.status <> 'completed'
                ORDER BY CASE j.status WHEN 'abandoned' THEN 0 WHEN 'deferred' THEN 1 WHEN 'failed' THEN 2 WHEN 'running' THEN 3 ELSE 4 END,
                         j.next_run_at
                LIMIT 1
            ) AS o ON true
            LEFT JOIN LATERAL (
                SELECT count(*)::int AS total, (count(*) FILTER (WHERE j.status = 'completed'))::int AS completed
                FROM {jobs} AS j
                WHERE j.queue = c.queue AND j.owner = c.owner AND j.type = c.type AND j.mode = 'initial'
            ) AS h ON true
            WHERE @after_key::timestamptz IS NULL
               OR (c.sort_key, c.queue, c.owner, c.type) > (@after_key, @after_queue, @after_owner, @after_type)
            ORDER BY c.sort_key, c.queue, c.owner, c.type
            LIMIT @limit
            """;

        Problems =
            $"""
            SELECT queue, code,
                   count(*)::int,
                   (count(*) FILTER (WHERE status = 'abandoned'))::int,
                   count(DISTINCT owner)::int,
                   min(updated_at), max(updated_at),
                   (array_agg(error ORDER BY updated_at DESC))[1]
            FROM (
                SELECT queue, owner, status, error, updated_at, coalesce({CodeOf("error")}, 'unknown') AS code
                FROM {jobs}
                WHERE status <> 'completed' AND status IN ('failed', 'deferred', 'abandoned')
                  AND (@queue::text IS NULL OR queue = @queue)
            ) AS p
            GROUP BY queue, code
            ORDER BY count(*) DESC, queue, code
            """;

        // Every change below is conditional on the row's own status at the moment of the write, so a job a worker takes in
        // the meantime is left alone. The status read beside it only explains a refusal.
        const string fresh =
            """
            status = 'pending',
                    attempt_count = 0,
                    next_run_at = @now,
                    error = NULL,
                    completed_at = NULL,
                    leased_by = NULL,
                    lease_expires_at = NULL,
                    updated_at = @now
            """;

        // Every repair writes who asked for it into the actions log, in the same statement as the change.
        string Logged(string action, string queue, string owner, string target, string changed) =>
            $"""
            logged AS (
                INSERT INTO {actions} (at, by, action, queue, owner, target, reason, changed)
                SELECT @now, @by, '{action}', {queue}, {owner}, {target}, @reason, {changed}
                WHERE {changed} > 0
            )
            """;

        RequeueJob =
            $"""
            WITH target AS (SELECT status FROM {jobs} WHERE id = @id),
            changed AS (
                UPDATE {jobs}
                SET {fresh}
                WHERE id = @id AND status IN ('failed', 'deferred', 'abandoned')
                RETURNING queue, owner
            ),
            {Logged("requeue job", "(SELECT queue FROM changed)", "(SELECT owner FROM changed)", "@id::text", "(SELECT count(*) FROM changed)")}
            SELECT t.status, (SELECT count(*)::int FROM changed) FROM target AS t
            """;

        // A skip deletes the job, so an ordered lane goes on, and keeps its trace in the actions log: who, why, and which
        // job of which type and key. A running job is never skipped; stop it with a pause first.
        SkipJob =
            $"""
            WITH target AS (SELECT status FROM {jobs} WHERE id = @id),
            removed AS (
                DELETE FROM {jobs}
                WHERE id = @id AND status IN ('pending', 'failed', 'deferred', 'abandoned')
                RETURNING queue, owner, type, dedup_key
            ),
            {Logged("skip job", "(SELECT queue FROM removed)", "(SELECT owner FROM removed)",
                "(SELECT @id::text || ' ' || type || coalesce(' ' || dedup_key, '') FROM removed)", "(SELECT count(*) FROM removed)")}
            SELECT t.status, (SELECT count(*)::int FROM removed) FROM target AS t
            """;

        RestartJob =
            $"""
            WITH target AS (SELECT status FROM {jobs} WHERE id = @id),
            changed AS (
                UPDATE {jobs}
                SET {fresh},
                    cursor = NULL,
                    processed_count = 0,
                    started_at = NULL
                WHERE id = @id AND status <> 'running'
                RETURNING queue, owner
            ),
            {Logged("restart job", "(SELECT queue FROM changed)", "(SELECT owner FROM changed)", "@id::text", "(SELECT count(*) FROM changed)")}
            SELECT t.status, (SELECT count(*)::int FROM changed) FROM target AS t
            """;

        RequeueByCode =
            $"""
            WITH changed AS (
                UPDATE {jobs}
                SET {fresh}
                WHERE queue = @queue
                  AND status <> 'completed' AND status IN ('failed', 'deferred', 'abandoned')
                  AND coalesce({CodeOf("error")}, 'unknown') = @code
                RETURNING 1
            ),
            {Logged("requeue by code", "@queue", "NULL", "@code", "(SELECT count(*) FROM changed)")}
            SELECT count(*)::int FROM changed
            """;

        // Jobs already moved forward are skipped, so asking twice changes nothing.
        PrioritizeOwner =
            $"""
            WITH changed AS (
                UPDATE {jobs}
                SET priority = priority - @boost, updated_at = @now
                WHERE queue = @queue AND owner = @owner
                  AND status <> 'completed' AND status IN ('pending', 'failed', 'deferred', 'abandoned')
                  AND priority >= @movable
                RETURNING 1
            ),
            {Logged("prioritize owner", "@queue", "@owner", "NULL", "(SELECT count(*) FROM changed)")}
            SELECT count(*)::int FROM changed
            """;

        // Two calls for one owner and type at once would both find nothing waiting; the lock, taken in the same
        // transaction before the statement below reads, lets the second join what the first made.
        SyncNowLock = "SELECT pg_advisory_xact_lock(hashtext('synclane.sync-now'), hashtext(@queue || chr(31) || @owner || chr(31) || @type))";

        // A sync-now covers from where the owner's completed data reaches to now; a type without windows (no planned
        // coverage) is fetched whole. One waits per owner and type: a later call stretches its window to now instead of
        // adding another. A failed one is left to its back-off. The lane is the owner's, from its newest job.
        SyncNow =
            $"""
            WITH known AS (
                SELECT covered_to, completed_to FROM {coverage}
                WHERE queue = @queue AND owner = @owner AND type = @type
            ),
            waiting AS (
                SELECT id FROM {jobs}
                WHERE queue = @queue AND owner = @owner AND type = @type
                  AND status = 'pending' AND mode = 'manual' AND starts_with(dedup_key, @prefix)
                ORDER BY id
                LIMIT 1
            ),
            joined AS (
                UPDATE {jobs} AS j
                SET window_end = CASE WHEN j.window_end IS NULL THEN NULL ELSE GREATEST(j.window_end, @now) END,
                    next_run_at = LEAST(j.next_run_at, @now),
                    priority = LEAST(j.priority, @priority),
                    updated_at = @now
                FROM waiting AS w
                WHERE j.id = w.id
                RETURNING j.id
            ),
            added AS (
                INSERT INTO {jobs}
                    (queue, owner, lane, type, mode, status, priority, window_start, window_end, dedup_key, requested_by, next_run_at, created_at, updated_at)
                SELECT @queue, @owner,
                       coalesce((SELECT lane FROM {jobs} WHERE queue = @queue AND owner = @owner ORDER BY id DESC LIMIT 1), @owner),
                       @type, 'manual', 'pending', @priority,
                       CASE WHEN k.covered_to IS NULL THEN NULL ELSE coalesce(k.completed_to, k.covered_to) END,
                       CASE WHEN k.covered_to IS NULL THEN NULL ELSE @now END,
                       @key, @by, @now, @now, @now
                FROM known AS k
                WHERE NOT EXISTS (SELECT 1 FROM waiting)
                RETURNING id
            ),
            {Logged("sync now", "@queue", "@owner", "@type", "((SELECT count(*) FROM joined) + (SELECT count(*) FROM added))")}
            SELECT id, false FROM added
            UNION ALL
            SELECT id, true FROM joined
            """;

        // A pause is announced to every instance in the same statement, so running jobs are cut at once.
        Pause =
            $"""
            WITH paused AS (
                INSERT INTO {pauses} AS p (queue, owner, paused_by, reason, paused_at, until)
                VALUES (@queue, @owner, @by, @reason, @now, @until)
                ON CONFLICT (queue, owner) DO UPDATE
                SET paused_by = excluded.paused_by, reason = excluded.reason, paused_at = excluded.paused_at, until = excluded.until
                RETURNING p.queue
            ),
            {Logged("pause", "@queue", "nullif(@owner, '')", "@until::text", "1")}
            SELECT count(*)::int
            FROM (SELECT pg_notify(@channel, json_build_object('control', 'pause', 'queue', @queue, 'owner', @owner)::text) FROM paused) AS announced
            """;

        Resume =
            $"""
            WITH resumed AS (
                DELETE FROM {pauses} WHERE queue = @queue AND owner = @owner
                RETURNING queue
            ),
            {Logged("resume", "@queue", "nullif(@owner, '')", "NULL", "(SELECT count(*) FROM resumed)")}
            SELECT count(*)::int
            FROM (SELECT pg_notify(@channel, json_build_object('control', 'resume', 'queue', @queue, 'owner', @owner)::text) FROM resumed) AS announced
            """;

        var limits = $"{schema}.{BudgetSql.LimitsTableName}";

        // An operator's limit only lowers a budget: the ceiling is the least of it and the budget's own.
        LimitBudget =
            $"""
            WITH limited AS (
                INSERT INTO {limits} AS l (budget, key, rate, until, set_by, reason, set_at)
                VALUES (@budget, @key, @rate, @until, @by, @reason, @now)
                ON CONFLICT (budget, key) DO UPDATE
                SET rate = excluded.rate, until = excluded.until, set_by = excluded.set_by, reason = excluded.reason, set_at = excluded.set_at
                RETURNING l.budget
            ),
            {Logged("limit budget", "NULL", "NULL", "@budget || ':' || @key || ' ' || @rate::text || '/s'", "(SELECT count(*) FROM limited)")}
            SELECT count(*)::int FROM limited
            """;

        ClearBudgetLimit =
            $"""
            WITH cleared AS (
                DELETE FROM {limits} WHERE budget = @budget AND key = @key
                RETURNING budget
            ),
            {Logged("clear budget limit", "NULL", "NULL", "@budget || ':' || @key", "(SELECT count(*) FROM cleared)")}
            SELECT count(*)::int FROM cleared
            """;

        BudgetLimits =
            $"""
            SELECT budget, key, rate, until, set_by, reason, set_at
            FROM {limits}
            WHERE until IS NULL OR until > @now
            ORDER BY budget, key
            """;

        Pauses =
            $"""
            SELECT queue, owner, paused_by, reason, paused_at, until
            FROM {pauses}
            WHERE until IS NULL OR until > @now
            ORDER BY queue, owner
            """;

        Actions =
            $"""
            SELECT id, at, by, action, queue, owner, target, reason, changed
            FROM {actions}
            WHERE (@queue::text IS NULL OR queue = @queue)
              AND (@owner::text IS NULL OR owner = @owner)
              AND (@before::bigint IS NULL OR id < @before)
            ORDER BY id DESC
            LIMIT @limit
            """;

        Volume =
            $"""
            SELECT date_trunc(@unit, hour, 'UTC'), sum(completed)::bigint, sum(records)::bigint, sum(failures)::bigint, sum(abandoned)::bigint
            FROM {stats}
            WHERE hour >= @from AND hour < @to
              AND (@queue::text IS NULL OR queue = @queue)
              AND (@type::text IS NULL OR type = @type)
            GROUP BY 1
            ORDER BY 1
            """;
    }

    /// <summary>Requeues one failed, deferred or abandoned job. Returns its status before, and whether it changed.</summary>
    public string RequeueJob { get; }

    /// <summary>Runs one job again from its first page, whatever its status but running.</summary>
    public string RestartJob { get; }

    public string SkipJob { get; }

    public string RequeueByCode { get; }

    public string PrioritizeOwner { get; }

    public string SyncNowLock { get; }

    public string SyncNow { get; }

    public string Overview { get; }

    public string Freshness { get; }

    public string Problems { get; }

    public string Volume { get; }

    public string Pause { get; }

    public string LimitBudget { get; }

    public string ClearBudgetLimit { get; }

    public string BudgetLimits { get; }

    public string Resume { get; }

    public string Pauses { get; }

    public string Actions { get; }

    /// <summary>The channel pauses and resumes are announced on.</summary>
    public string Channel { get; }

    /// <summary>A job's error code: the first SyncLane code in its error, else the exception type before the first colon.</summary>
    private static string CodeOf(string column) =>
        $"coalesce(substring({column} from 'SL[0-9]{{4}}'), nullif(split_part({column}, ':', 1), ''))";
}
