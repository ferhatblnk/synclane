using Npgsql;
using NpgsqlTypes;

namespace SyncLane.Storage;

/// <summary>
/// Reads and writes the rate buckets. Every call is one round trip: the row of a bucket nobody has used yet is created
/// in the same batch as the statement that needs it.
/// </summary>
internal sealed class BudgetStore(NpgsqlDataSource dataSource, BudgetSql sql)
{
    /// <summary>Takes a token from the bucket, now or when it is due, unless that is further away than <paramref name="maxWait"/>.</summary>
    public async Task<Reservation> ReserveAsync(
        BudgetDescription budget, string key, DateTimeOffset now, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(Seed(budget, key, now));

        var reserve = Command(sql.Reserve, budget.Name, key);
        reserve.Parameters.AddWithValue("burst", (double)budget.Burst);
        reserve.Parameters.AddWithValue("per_second", PerSecond(budget));
        reserve.Parameters.AddWithValue("now", now.ToUniversalTime());
        reserve.Parameters.AddWithValue("max_wait", maxWait.TotalSeconds);
        batch.BatchCommands.Add(reserve);

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new SyncLaneException(SyncLaneErrors.BucketRowMissing(sql.Schema, budget.Name));

        return new Reservation(reader.GetBoolean(0), TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(1))), reader.GetInt64(2));
    }

    /// <summary>How many pauses the bucket has had, or <c>null</c> when it has no row.</summary>
    public async Task<long?> GenerationAsync(string budget, string key, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Generation);
        command.Parameters.AddWithValue("budget", budget);
        command.Parameters.AddWithValue("key", key);
        return await command.ExecuteScalarAsync(cancellationToken) is long generation ? generation : null;
    }

    /// <summary>
    /// Pauses the bucket because the API answered "slow down" to a request sent in <paramref name="generation"/>.
    /// Returns whether the pause started or grew, and when it ends.
    /// </summary>
    public async Task<BudgetPause> PauseAsync(
        BudgetDescription budget,
        string key,
        long generation,
        DateTimeOffset now,
        TimeSpan? retryAfter,
        TimeSpan firstBackoff,
        TimeSpan maxBackoff,
        CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(Seed(budget, key, now));

        var pause = Command(sql.Pause, budget.Name, key);
        pause.Parameters.AddWithValue("generation", generation);
        pause.Parameters.AddWithValue("now", now.ToUniversalTime());
        pause.Parameters.Add(new NpgsqlParameter("retry_after", NpgsqlDbType.Interval) { Value = retryAfter is { } asked ? asked : DBNull.Value });
        pause.Parameters.AddWithValue("first_backoff", firstBackoff);
        pause.Parameters.AddWithValue("max_backoff", maxBackoff);
        batch.BatchCommands.Add(pause);

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new SyncLaneException(SyncLaneErrors.BucketRowMissing(sql.Schema, budget.Name));

        return new BudgetPause(reader.GetBoolean(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    private NpgsqlBatchCommand Seed(BudgetDescription budget, string key, DateTimeOffset now)
    {
        var seed = Command(sql.Seed, budget.Name, key);
        seed.Parameters.AddWithValue("burst", (double)budget.Burst);
        seed.Parameters.AddWithValue("now", now.ToUniversalTime());
        return seed;
    }

    private static NpgsqlBatchCommand Command(string statement, string budget, string key)
    {
        var command = new NpgsqlBatchCommand(statement);
        command.Parameters.AddWithValue("budget", budget);
        command.Parameters.AddWithValue("key", key);
        return command;
    }

    private static double PerSecond(BudgetDescription budget) => budget.Limit / budget.Per.TotalSeconds;
}

/// <summary>A token taken from a bucket: whether it was granted, how long until it is due, and the pause it belongs to.</summary>
internal readonly record struct Reservation(bool Granted, TimeSpan Wait, long Generation);

/// <summary>What a throttle did to a bucket: whether it paused it (or made the pause longer), and when the pause ends.</summary>
internal readonly record struct BudgetPause(bool Paused, DateTimeOffset Until);
