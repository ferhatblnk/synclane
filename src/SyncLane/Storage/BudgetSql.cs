namespace SyncLane.Storage;

/// <summary>
/// The statements behind rate budgets, built once for the configured schema. Each one locks the bucket's row, so
/// instances that draw from one bucket at the same moment take their turns instead of both taking the last token.
/// </summary>
internal sealed class BudgetSql
{
    public const string TableName = "rate_buckets";

    public BudgetSql(string schema)
    {
        Schema = schema;
        var table = $"{schema}.{TableName}";

        // A bucket nobody has used starts full. Sent in the same batch as the statement after it, which sees the row.
        Seed =
            $"""
            INSERT INTO {table} (budget, key, tokens, refilled_at, updated_at)
            VALUES (@budget, @key, @burst, @now, @now)
            ON CONFLICT (budget, key) DO NOTHING
            """;

        // Refills the bucket up to now, then takes a token unless it is further away than @max_wait. A token that is
        // not free yet is taken anyway and the count goes below zero, so waiters queue in order. refilled_at stays in
        // the future during a pause, and nothing refills until it ends.
        Reserve =
            $"""
            WITH current AS (
                SELECT budget, key, generation, taken,
                       LEAST(@burst, tokens + GREATEST(EXTRACT(EPOCH FROM @now - refilled_at)::double precision, 0) * @per_second) AS tokens,
                       GREATEST(refilled_at, @now) AS refilled_at
                FROM {table}
                WHERE budget = @budget AND key = @key
                FOR UPDATE
            ),
            decided AS (
                SELECT c.*,
                       EXTRACT(EPOCH FROM c.refilled_at - @now)::double precision + GREATEST(1 - c.tokens, 0) / @per_second AS wait
                FROM current AS c
            )
            UPDATE {table} AS b
            SET tokens = CASE WHEN d.wait <= @max_wait THEN d.tokens - 1 ELSE d.tokens END,
                taken = CASE WHEN d.wait <= @max_wait THEN d.taken + 1 ELSE d.taken END,
                refilled_at = d.refilled_at,
                updated_at = @now
            FROM decided AS d
            WHERE b.budget = d.budget AND b.key = d.key
            RETURNING d.wait <= @max_wait, d.wait, d.generation, d.taken + 1
            """;

        Generation = $"SELECT generation FROM {table} WHERE budget = @budget AND key = @key";

        Observe =
            $"""
            WITH current AS (
                SELECT budget, key, generation, taken, refilled_at,
                       LEAST(@burst, tokens + GREATEST(EXTRACT(EPOCH FROM @now - refilled_at)::double precision, 0) * @per_second) AS tokens
                FROM {table}
                WHERE budget = @budget AND key = @key
                FOR UPDATE
            ),
            decided AS (
                SELECT c.*,
                       @remaining - (c.taken - @sequence) AS left,
                       @remaining - (c.taken - @sequence) < 1 AND COALESCE(@reset_at::timestamptz > GREATEST(c.refilled_at, @now), false) AS pause
                FROM current AS c
            ),
            observed AS (
                UPDATE {table} AS b
                SET tokens = CASE WHEN d.pause THEN 0 ELSE LEAST(d.tokens, d.left) END,
                    refilled_at = CASE WHEN d.pause THEN @reset_at::timestamptz ELSE GREATEST(d.refilled_at, @now) END,
                    generation = CASE WHEN d.pause OR (d.left < 1 AND d.left <= d.tokens - 1) THEN d.generation + 1 ELSE d.generation END,
                    updated_at = @now
                FROM decided AS d
                WHERE b.budget = d.budget AND b.key = d.key
                  AND (d.pause OR d.left < d.tokens)
                RETURNING d.pause, b.refilled_at
            )
            SELECT pause, refilled_at FROM observed
            """;

        // A throttle answered to a request sent after the last pause began starts a new pause: for @retry_after, or a
        // backoff that doubles while throttles keep coming. One answered to a request sent before it is already
        // covered: it only lengthens the pause when it asks for longer. Every pause empties the bucket and counts a
        // generation, which voids the reservations made before it.
        Pause =
            $"""
            WITH current AS (
                SELECT budget, key, refilled_at, generation,
                       generation = @generation AS fresh,
                       CASE WHEN throttled_at IS NULL OR @now - throttled_at > last_pause * 2 THEN 1 ELSE streak + 1 END AS streak
                FROM {table}
                WHERE budget = @budget AND key = @key
                FOR UPDATE
            ),
            decided AS (
                SELECT c.*,
                       CASE WHEN @retry_after IS NOT NULL THEN @retry_after
                            WHEN c.fresh THEN LEAST(@max_backoff, @first_backoff * power(2, LEAST(c.streak - 1, 20)))
                            ELSE interval '0' END AS pause
                FROM current AS c
            ),
            paused AS (
                UPDATE {table} AS b
                SET tokens = 0,
                    refilled_at = GREATEST(d.refilled_at, @now + d.pause),
                    generation = d.generation + 1,
                    streak = CASE WHEN d.fresh THEN d.streak ELSE b.streak END,
                    throttled_at = CASE WHEN d.fresh THEN @now ELSE b.throttled_at END,
                    last_pause = CASE WHEN d.fresh THEN d.pause ELSE b.last_pause END,
                    updated_at = @now
                FROM decided AS d
                WHERE b.budget = d.budget AND b.key = d.key
                  AND (d.fresh OR @now + d.pause > d.refilled_at)
                RETURNING true AS paused, b.refilled_at
            )
            SELECT paused, refilled_at FROM paused
            UNION ALL
            SELECT false, refilled_at FROM decided WHERE NOT (fresh OR @now + pause > refilled_at)
            """;
    }

    public string Schema { get; }

    public string Seed { get; }

    /// <summary>
    /// What the API said is left, applied to the bucket. Tokens taken after the answered request (<c>@sequence</c>) have
    /// not reached the API's count yet, so they are subtracted from it. The bucket is only ever lowered. With nothing left
    /// and a reset ahead, it pauses until the reset and restarts empty; the pause counts a generation, so waiters queue
    /// again behind it. A lowering that leaves less than one token and takes at least one away also counts a generation,
    /// because waiters were promised tokens that are gone. Nothing is written when the reading changes nothing.
    /// </summary>
    public string Observe { get; }

    public string Reserve { get; }

    public string Generation { get; }

    public string Pause { get; }
}
