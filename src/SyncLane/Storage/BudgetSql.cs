namespace SyncLane.Storage;

/// <summary>
/// The statements behind rate budgets, built once for the configured schema. Each one locks the bucket's row, so
/// instances that draw from one bucket at the same moment take their turns instead of both taking the last token.
/// </summary>
/// <remarks>
/// A bucket refills at its current rate, which it learns: it never goes above the declared limit (<c>@ceiling</c>, in
/// tokens a second), drops to 0.7 times itself on a fresh throttle (not below 1% of the limit), and rises with every
/// token taken while the bucket is the bottleneck. The rise is 2% per token below 90% of the rate that last caused a
/// throttle, 0.03% from there up to it, and 0.1% above it. The numbers come from a simulation against servers with
/// hidden limits, and LearningGateTests guards them; change them only with a new measurement.
/// </remarks>
internal sealed class BudgetSql
{
    public const string TableName = "rate_buckets";

    /// <summary>The bucket's current rate. A row written before rates were learned has none and starts afresh.</summary>
    private const string CurrentRate = "LEAST(@ceiling, COALESCE(rate, @start_rate))";

    /// <summary>The tokens in the bucket now: refilled at the current rate since the last refill, up to the burst.</summary>
    private const string Refilled =
        $"LEAST(@burst, tokens + GREATEST(EXTRACT(EPOCH FROM @now - refilled_at)::double precision, 0) * {CurrentRate})";

    public BudgetSql(string schema)
    {
        Schema = schema;
        var table = $"{schema}.{TableName}";

        // A bucket nobody has used starts full, at its starting rate. Sent in the same batch as the statement after it,
        // which sees the row.
        Seed =
            $"""
            INSERT INTO {table} (budget, key, tokens, refilled_at, rate, updated_at)
            VALUES (@budget, @key, @burst, @now, @start_rate, @now)
            ON CONFLICT (budget, key) DO NOTHING
            """;

        // Refills the bucket up to now, then takes a token unless it is further away than @max_wait. A token that is
        // not free yet is taken anyway and the count goes below zero, so waiters queue in order. refilled_at stays in
        // the future during a pause, and nothing refills until it ends. A token taken while the bucket was empty and
        // not paused means demand met the rate, so the rate rises; an idle budget does not.
        Reserve =
            $"""
            WITH current AS (
                SELECT budget, key, generation, taken,
                       {CurrentRate} AS rate,
                       LEAST(@ceiling, COALESCE(rate_max, @ceiling)) AS rate_max,
                       {Refilled} AS tokens,
                       GREATEST(refilled_at, @now) AS refilled_at,
                       refilled_at <= @now AS flowing
                FROM {table}
                WHERE budget = @budget AND key = @key
                FOR UPDATE
            ),
            decided AS (
                SELECT c.*,
                       EXTRACT(EPOCH FROM c.refilled_at - @now)::double precision + GREATEST(1 - c.tokens, 0) / c.rate AS wait,
                       c.flowing AND c.tokens < 1 AS bottleneck
                FROM current AS c
            )
            UPDATE {table} AS b
            SET tokens = CASE WHEN d.wait <= @max_wait THEN d.tokens - 1 ELSE d.tokens END,
                taken = CASE WHEN d.wait <= @max_wait THEN d.taken + 1 ELSE d.taken END,
                rate = CASE WHEN d.wait <= @max_wait AND d.bottleneck
                            THEN LEAST(@ceiling, d.rate * CASE WHEN d.rate < 0.9 * d.rate_max THEN 1.02
                                                               WHEN d.rate < d.rate_max THEN 1.0003
                                                               ELSE 1.001 END)
                            ELSE d.rate END,
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
                       {Refilled} AS tokens
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

        // A throttle answered to a request sent after the last pause began is fresh: it cuts the rate, remembers the
        // rate it cut from, and starts a new pause for @retry_after, or for one token's time at least @min_backoff. The
        // pause doubles only while the first request after the previous pause is the one refused (@sequence right after
        // taken_at_pause): then the API is still full. One answered to a request sent before the pause is already
        // covered: it only lengthens the pause when it asks for longer. Every pause empties the bucket and counts a
        // generation, which voids the reservations made before it.
        Pause =
            $"""
            WITH current AS (
                SELECT budget, key, refilled_at, generation, taken, taken_at_pause, streak,
                       {CurrentRate} AS rate,
                       generation = @generation AS fresh
                FROM {table}
                WHERE budget = @budget AND key = @key
                FOR UPDATE
            ),
            decided AS (
                SELECT c.*,
                       CASE WHEN @sequence = c.taken_at_pause + 1 THEN c.streak + 1 ELSE 1 END AS next_streak
                FROM current AS c
            ),
            timed AS (
                SELECT d.*,
                       CASE WHEN @retry_after IS NOT NULL THEN @retry_after
                            WHEN d.fresh THEN LEAST(@max_backoff,
                                GREATEST(@min_backoff, make_interval(secs => 1 / d.rate)) * power(2, LEAST(d.next_streak - 1, 20)))
                            ELSE interval '0' END AS pause
                FROM decided AS d
            ),
            paused AS (
                UPDATE {table} AS b
                SET tokens = 0,
                    refilled_at = GREATEST(t.refilled_at, @now + t.pause),
                    generation = t.generation + 1,
                    streak = CASE WHEN t.fresh THEN t.next_streak ELSE b.streak END,
                    rate = CASE WHEN t.fresh THEN GREATEST(@ceiling * 0.01, t.rate * 0.7) ELSE b.rate END,
                    rate_max = CASE WHEN t.fresh THEN t.rate ELSE b.rate_max END,
                    taken_at_pause = CASE WHEN t.fresh THEN t.taken ELSE b.taken_at_pause END,
                    throttled_at = CASE WHEN t.fresh THEN @now ELSE b.throttled_at END,
                    last_pause = CASE WHEN t.fresh THEN t.pause ELSE b.last_pause END,
                    updated_at = @now
                FROM timed AS t
                WHERE b.budget = t.budget AND b.key = t.key
                  AND (t.fresh OR @now + t.pause > t.refilled_at)
                RETURNING true AS paused, b.refilled_at, COALESCE(b.rate, t.rate) AS rate
            )
            SELECT paused, refilled_at, rate FROM paused
            UNION ALL
            SELECT false, refilled_at, rate FROM timed WHERE NOT (fresh OR @now + pause > refilled_at)
            """;
    }

    public string Schema { get; }

    public string Seed { get; }

    /// <summary>
    /// What the API said is left, applied to the bucket. Tokens taken after the answered request (<c>@sequence</c>) have
    /// not reached the API's count yet, so they are subtracted from it. The bucket is only ever lowered. With nothing left
    /// and a reset ahead, it pauses until the reset and restarts empty; the pause counts a generation, so waiters queue
    /// again behind it. A lowering that leaves less than one token and takes at least one away also counts a generation,
    /// because waiters were promised tokens that are gone. A spent window is quota, not rate: the rate does not change.
    /// Nothing is written when the reading changes nothing.
    /// </summary>
    public string Observe { get; }

    public string Reserve { get; }

    public string Generation { get; }

    public string Pause { get; }
}
