using Npgsql;
using NpgsqlTypes;
using SyncLane.Planning;

namespace SyncLane.Storage;

/// <summary>
/// Reads and writes the jobs table. Every write a worker makes to a job it holds is conditional on still holding it,
/// so a worker whose lease was taken over cannot overwrite the new holder.
/// </summary>
internal sealed class JobStore(NpgsqlDataSource dataSource, JobSql sql) : IAsyncDisposable
{
    private const int LeaseRaceRetries = 3;

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    public string Schema => sql.Schema;

    /// <summary>
    /// Applies the steps this database is missing, in one transaction, under a lock that makes instances starting at
    /// the same time wait for each other. Returns the number of steps applied.
    /// </summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await RunAsync($"{sql.LockSchema};\n{Migrations.HistoryTable(sql.Schema)}");
        var applied = await AppliedAsync(connection, transaction, cancellationToken);

        var missing = Migrations.All.Where(migration => !applied.Contains(migration.Version)).ToList();
        if (missing.Count > 0)
            await RunAsync(string.Join('\n', missing.Select(migration => $"{migration.Sql(sql.Schema)}\n{Migrations.Record(sql.Schema, migration)}")));

        await transaction.CommitAsync(cancellationToken);
        return missing.Count;

        async Task RunAsync(string statement)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>The versions this SyncLane knows that the database has not recorded, oldest first.</summary>
    public async Task<IReadOnlyList<int>> MissingMigrationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var exists = new NpgsqlCommand(sql.HasMigrationTable, connection))
        {
            exists.Parameters.AddWithValue("name", $"{sql.Schema}.{Migrations.TableName}");
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
                return Migrations.All.Select(migration => migration.Version).ToList();
        }

        var applied = await AppliedAsync(connection, null, cancellationToken);
        return Migrations.All.Select(migration => migration.Version).Where(version => !applied.Contains(version)).ToList();
    }

    private async Task<HashSet<int>> AppliedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.AppliedMigrations, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var versions = new HashSet<int>();
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
            versions.Add(reader.GetInt32(0));

        return versions;
    }

    /// <summary>Inserts the jobs that do not exist yet, in one statement. Returns how many were created.</summary>
    public async Task<int> EnqueueAsync(string queue, IReadOnlyList<PlannedJob> jobs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
            return 0;

        await using var command = EnqueueCommand(queue, jobs, now);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Inserts one job. Returns its id, or <c>null</c> when a job with the same dedup key exists.</summary>
    public async Task<long?> EnqueueOneAsync(string queue, PlannedJob job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = EnqueueCommand(queue, [job], now);
        return await command.ExecuteScalarAsync(cancellationToken) is long id ? id : null;
    }

    /// <summary>
    /// Takes the next job of the queue, or returns <c>null</c> when nothing is due.
    /// Two workers can pick different jobs of one lane in the same instant. The index on running jobs lets one of them
    /// through; the other asks again and no longer sees that lane.
    /// </summary>
    public async Task<Job?> LeaseAsync(string queue, string worker, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        // Sequential: a retry; the next try only happens when this one lost the race for a lane.
        for (var race = 0; race < LeaseRaceRetries; race++)
        {
            try
            {
                await using var command = dataSource.CreateCommand(sql.Lease);
                command.Parameters.AddWithValue("queue", queue);
                command.Parameters.AddWithValue("worker", worker);
                command.Parameters.AddWithValue("now", now.ToUniversalTime());
                command.Parameters.AddWithValue("lease_expires_at", leaseExpiresAt.ToUniversalTime());

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
            }
        }

        return null;
    }

    public async Task<bool> CheckpointAsync(
        long id, string worker, string? cursor, int processed, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        await using var command = Held(sql.Checkpoint, id, worker, now);
        command.Parameters.Add(Text("cursor", cursor));
        command.Parameters.AddWithValue("processed", processed);
        command.Parameters.AddWithValue("lease_expires_at", leaseExpiresAt.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> HeartbeatAsync(long id, string worker, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        await using var command = Held(sql.Heartbeat, id, worker, now);
        command.Parameters.AddWithValue("lease_expires_at", leaseExpiresAt.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> CompleteAsync(long id, string worker, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Held(sql.Complete, id, worker, now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Ends an attempt that did not complete: failed, deferred or abandoned.</summary>
    public async Task<bool> SettleAsync(
        long id, string worker, JobStatus status, string error, DateTimeOffset nextRunAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Held(sql.Settle, id, worker, now);
        command.Parameters.AddWithValue("status", JobText.Of(status));
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("next_run_at", nextRunAt.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Hands a job back untouched, because the worker is stopping. The attempt is not counted.</summary>
    public async Task<bool> ReleaseAsync(long id, string worker, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Held(sql.Release, id, worker, now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Hands a job back to run at <paramref name="nextRunAt"/>, because it has to wait. The attempt is not counted.</summary>
    public async Task<bool> PostponeAsync(
        long id, string worker, string reason, DateTimeOffset nextRunAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = Held(sql.Postpone, id, worker, now);
        command.Parameters.AddWithValue("error", reason);
        command.Parameters.AddWithValue("next_run_at", nextRunAt.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> RequeueAsync(string queue, string? owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Requeue);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.Add(Text("owner", owner));
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CancelAsync(string queue, string owner, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Cancel);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Dictionary<CoverageKey, TypeCoverage>> GetCoverageAsync(string queue, IReadOnlyCollection<string> owners, CancellationToken cancellationToken)
    {
        var coverage = new Dictionary<CoverageKey, TypeCoverage>();
        if (owners.Count == 0)
            return coverage;

        await using var command = dataSource.CreateCommand(sql.Coverage);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owners", owners.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            coverage[new CoverageKey(reader.GetString(0), reader.GetString(1))] = new TypeCoverage(
                reader.GetBoolean(2),
                Moment(reader, 3),
                Moment(reader, 4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                Moment(reader, 7),
                Moment(reader, 8));
        }

        return coverage;
    }

    public async Task<OwnerProgress> GetProgressAsync(string queue, string owner, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Progress);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owner", owner);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new OwnerProgress(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    /// <summary>
    /// Reads up to <paramref name="limit"/> jobs older than <paramref name="before"/>, newest first, and the number of
    /// matching jobs when <paramref name="countTotal"/> is set. Both statements go to the database in one round trip.
    /// </summary>
    public async Task<(List<Job> Jobs, long? Total)> GetJobsAsync(
        JobQuery query, long? before, int limit, bool countTotal, CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();

        var list = new NpgsqlBatchCommand(sql.List);
        AddFilters(list.Parameters, query);
        list.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Bigint) { Value = (object?)before ?? DBNull.Value });
        list.Parameters.AddWithValue("limit", limit);
        batch.BatchCommands.Add(list);

        if (countTotal)
        {
            var count = new NpgsqlBatchCommand(sql.Count);
            AddFilters(count.Parameters, query);
            batch.BatchCommands.Add(count);
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);

        var jobs = new List<Job>(limit);
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
            jobs.Add(Read(reader));

        long? total = null;
        if (countTotal && await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
            total = reader.GetInt64(0);

        return (jobs, total);

        static void AddFilters(NpgsqlParameterCollection parameters, JobQuery query)
        {
            parameters.Add(Text("queue", query.Queue));
            parameters.Add(Text("owner", query.Owner));
            parameters.Add(Text("status", query.Status is { } status ? JobText.Of(status) : null));
        }
    }

    public async Task<Job?> FindAsync(long id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Find);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private NpgsqlCommand Held(string statement, long id, string worker, DateTimeOffset now)
    {
        var command = dataSource.CreateCommand(statement);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", worker);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        return command;
    }

    private NpgsqlCommand EnqueueCommand(string queue, IReadOnlyList<PlannedJob> jobs, DateTimeOffset now)
    {
        var command = dataSource.CreateCommand(sql.Enqueue);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("owners", jobs.Select(job => job.Owner).ToArray());
        command.Parameters.AddWithValue("lanes", jobs.Select(job => job.Lane).ToArray());
        command.Parameters.AddWithValue("types", jobs.Select(job => job.Type).ToArray());
        command.Parameters.AddWithValue("modes", jobs.Select(job => JobText.Of(job.Mode)).ToArray());
        command.Parameters.AddWithValue("priorities", jobs.Select(job => job.Priority).ToArray());
        command.Parameters.Add(Moments("window_starts", jobs.Select(job => job.WindowStart)));
        command.Parameters.Add(Moments("window_ends", jobs.Select(job => job.WindowEnd)));
        command.Parameters.Add(new NpgsqlParameter("dedup_keys", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = jobs.Select(job => job.DedupKey).ToArray()
        });

        return command;
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Moments(string name, IEnumerable<DateTimeOffset?> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            Value = values.Select(value => value?.ToUniversalTime()).ToArray()
        };

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? Moment(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static Job Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Queue = reader.GetString(1),
        Owner = reader.GetString(2),
        Lane = reader.GetString(3),
        Type = reader.GetString(4),
        Mode = JobText.Mode(reader.GetString(5)),
        Status = JobText.Status(reader.GetString(6)),
        Priority = reader.GetInt32(7),
        WindowStart = Moment(reader, 8),
        WindowEnd = Moment(reader, 9),
        DedupKey = Text(reader, 10),
        Cursor = Text(reader, 11),
        ProcessedCount = reader.GetInt64(12),
        AttemptCount = reader.GetInt32(13),
        Error = Text(reader, 14),
        NextRunAt = reader.GetFieldValue<DateTimeOffset>(15),
        LeasedBy = Text(reader, 16),
        LeaseExpiresAt = Moment(reader, 17),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(18),
        StartedAt = Moment(reader, 19),
        HeartbeatAt = Moment(reader, 20),
        CompletedAt = Moment(reader, 21)
    };
}
