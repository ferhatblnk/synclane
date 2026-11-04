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
                WHERE heartbeat_at > @alive_since
                GROUP BY q
            )
            SELECT queue,
                   coalesce(owners, 0)::int, coalesce(instances, 0)::int,
                   coalesce(due, 0)::int, coalesce(postponed, 0)::int, coalesce(running, 0)::int,
                   coalesce(retrying, 0)::int, coalesce(deferred, 0)::int, coalesce(abandoned, 0)::int,
                   coalesce(completed, 0)::int, coalesce(records, 0)::bigint, coalesce(failures, 0)::int
            FROM open
            FULL JOIN owners USING (queue)
            FULL JOIN day USING (queue)
            FULL JOIN live USING (queue)
            ORDER BY queue
            """;

        // Coverage is sorted first and only the rows of the page look up their open job and their history.
        Freshness =
            $"""
            SELECT c.queue, c.owner, c.type, c.completed_to, c.completed_at,
                   o.status, {CodeOf("o.error")}, o.error, o.next_run_at,
                   coalesce(h.total, 0), coalesce(h.completed, 0),
                   c.sort_key
            FROM (
                SELECT queue, owner, type, completed_to, completed_at, coalesce(completed_to, completed_at, @never) AS sort_key
                FROM {coverage}
                WHERE (@queue::text IS NULL OR queue = @queue)
                  AND (@owner::text IS NULL OR owner = @owner)
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

    public string Overview { get; }

    public string Freshness { get; }

    public string Problems { get; }

    public string Volume { get; }

    /// <summary>A job's error code: the first SyncLane code in its error, else the exception type before the first colon.</summary>
    private static string CodeOf(string column) =>
        $"coalesce(substring({column} from 'SL[0-9]{{4}}'), nullif(split_part({column}, ':', 1), ''))";
}
