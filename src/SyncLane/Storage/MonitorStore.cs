using Npgsql;
using NpgsqlTypes;

namespace SyncLane.Storage;

/// <summary>Runs the monitor's statements. It borrows the job store's connection pool.</summary>
internal sealed class MonitorStore(NpgsqlDataSource dataSource, MonitorSql sql)
{
    public async Task<List<QueueOverview>> OverviewAsync(DateTimeOffset now, TimeSpan goneAfter, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Overview);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("since", (now - TimeSpan.FromDays(1)).ToUniversalTime());
        command.Parameters.AddWithValue("alive_since", (now - goneAfter).ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var queues = new List<QueueOverview>();
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            queues.Add(new QueueOverview
            {
                Queue = reader.GetString(0),
                Owners = reader.GetInt32(1),
                Instances = reader.GetInt32(2),
                Due = reader.GetInt32(3),
                Postponed = reader.GetInt32(4),
                Running = reader.GetInt32(5),
                Retrying = reader.GetInt32(6),
                Deferred = reader.GetInt32(7),
                Abandoned = reader.GetInt32(8),
                CompletedLastDay = reader.GetInt32(9),
                RecordsLastDay = reader.GetInt64(10),
                FailuresLastDay = reader.GetInt32(11),
                Pause = reader.IsDBNull(12) ? null : new PauseInfo
                {
                    Queue = reader.GetString(0),
                    By = reader.GetString(12),
                    Reason = reader.GetString(13),
                    PausedAt = reader.GetFieldValue<DateTimeOffset>(14),
                    Until = Moment(reader, 15)
                }
            });
        }

        return queues;
    }

    /// <summary>A page of owners, oldest data first, with each row's sort key for the next cursor.</summary>
    public async Task<List<(OwnerFreshness Row, DateTimeOffset SortKey)>> FreshnessAsync(
        string? queue, string? owner, string? search, (DateTimeOffset Key, string Queue, string Owner, string Type)? after, int limit,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Freshness);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.Add(Text("queue", queue));
        command.Parameters.Add(Text("owner", owner));
        command.Parameters.Add(Text("search", search is null ? null : JobStore.ContainsPattern(search)));
        command.Parameters.AddWithValue("never", MonitorSql.Never);
        command.Parameters.Add(new NpgsqlParameter("after_key", NpgsqlDbType.TimestampTz) { Value = (object?)after?.Key.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(Text("after_queue", after?.Queue));
        command.Parameters.Add(Text("after_owner", after?.Owner));
        command.Parameters.Add(Text("after_type", after?.Type));
        command.Parameters.AddWithValue("limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(OwnerFreshness, DateTimeOffset)>(limit);
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            var completedTo = Moment(reader, 3);
            var completedAt = Moment(reader, 4);
            rows.Add((new OwnerFreshness
            {
                Queue = reader.GetString(0),
                Owner = reader.GetString(1),
                OwnerName = Text(reader, 13),
                Type = reader.GetString(2),
                DataUntil = completedTo ?? completedAt,
                LastCompletedAt = completedAt,
                Status = reader.IsDBNull(5) ? null : JobText.Status(reader.GetString(5)),
                Code = Text(reader, 6),
                Error = Text(reader, 7),
                NextRunAt = Moment(reader, 8),
                HistoryTotal = reader.GetInt32(9),
                HistoryCompleted = reader.GetInt32(10),
                Paused = reader.GetBoolean(12)
            }, reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return rows;
    }

    public async Task<List<ProblemGroup>> ProblemsAsync(string? queue, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Problems);
        command.Parameters.Add(Text("queue", queue));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<ProblemGroup>();
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new ProblemGroup
            {
                Queue = reader.GetString(0),
                Code = reader.GetString(1),
                Jobs = reader.GetInt32(2),
                Abandoned = reader.GetInt32(3),
                Owners = reader.GetInt32(4),
                FirstSeen = reader.GetFieldValue<DateTimeOffset>(5),
                LastSeen = reader.GetFieldValue<DateTimeOffset>(6),
                Example = Text(reader, 7)
            });
        }

        return groups;
    }

    public async Task<List<VolumeBucket>> VolumeAsync(VolumeQuery query, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Volume);
        command.Parameters.AddWithValue("unit", query.Unit == VolumeUnit.Day ? "day" : "hour");
        command.Parameters.AddWithValue("from", query.From.ToUniversalTime());
        command.Parameters.AddWithValue("to", to.ToUniversalTime());
        command.Parameters.Add(Text("queue", query.Queue));
        command.Parameters.Add(Text("type", query.Type));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buckets = new List<VolumeBucket>();
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(new VolumeBucket
            {
                Start = reader.GetFieldValue<DateTimeOffset>(0),
                Completed = reader.GetInt64(1),
                Records = reader.GetInt64(2),
                Failures = reader.GetInt64(3),
                Abandoned = reader.GetInt64(4)
            });
        }

        return buckets;
    }

    /// <summary>Runs a change to one job. Returns the job's status before the change, or <c>null</c> when it does not exist.</summary>
    public async Task<(string? Status, bool Changed)> ChangeJobAsync(
        string statement, long id, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(statement);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        AddWho(command.Parameters, by, null);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetInt32(1) == 1) : (null, false);
    }

    /// <summary>Deletes one job that is not running and logs it with the reason. Returns its status before, and whether it went.</summary>
    public async Task<(string? Status, bool Skipped)> SkipJobAsync(long id, string by, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.SkipJob);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        AddWho(command.Parameters, by, reason);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetInt32(1) == 1) : (null, false);
    }

    public async Task<int> RequeueByCodeAsync(string queue, string code, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.RequeueByCode);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        AddWho(command.Parameters, by, null);
        return await command.ExecuteScalarAsync(cancellationToken) is int changed ? changed : 0;
    }

    public async Task<int> PrioritizeOwnerAsync(string queue, string owner, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.PrioritizeOwner);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("boost", Planning.Priorities.Boost);
        command.Parameters.AddWithValue("movable", Planning.Priorities.Movable);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        AddWho(command.Parameters, by, null);
        return await command.ExecuteScalarAsync(cancellationToken) is int changed ? changed : 0;
    }

    /// <summary>
    /// The waiting sync-now of an owner and type, made or stretched to now, and whether it already waited; <c>null</c>
    /// when the queue never planned that owner and type. Lock and statement go in one round trip and one transaction.
    /// </summary>
    public async Task<(long Id, bool Joined)?> SyncNowAsync(
        string queue, string owner, string type, int priority, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();

        var lockCommand = new NpgsqlBatchCommand(sql.SyncNowLock);
        lockCommand.Parameters.AddWithValue("queue", queue);
        lockCommand.Parameters.AddWithValue("owner", owner);
        lockCommand.Parameters.AddWithValue("type", type);
        batch.BatchCommands.Add(lockCommand);

        var sync = new NpgsqlBatchCommand(sql.SyncNow);
        sync.Parameters.AddWithValue("queue", queue);
        sync.Parameters.AddWithValue("owner", owner);
        sync.Parameters.AddWithValue("type", type);
        sync.Parameters.AddWithValue("priority", priority);
        sync.Parameters.AddWithValue("prefix", Planning.DedupKeys.SyncNowPrefix);
        sync.Parameters.AddWithValue("key", Planning.DedupKeys.SyncNow(Guid.NewGuid()));
        sync.Parameters.AddWithValue("now", now.ToUniversalTime());
        AddWho(sync.Parameters, by, null);
        batch.BatchCommands.Add(sync);

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt64(0), reader.GetBoolean(1)) : null;
    }

    /// <summary>Sets or replaces the pause of a queue (owner empty) or an owner, logs it and announces it. Returns 1.</summary>
    public async Task<int> PauseAsync(
        string queue, string owner, string by, string reason, DateTimeOffset? until, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Pause);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = (object?)until?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("channel", sql.Channel);
        AddWho(command.Parameters, by, reason);
        return await command.ExecuteScalarAsync(cancellationToken) is int paused ? paused : 0;
    }

    /// <summary>Lifts the pause of a queue (owner empty) or an owner, logs it and announces it. Returns 1, or 0 when there was none.</summary>
    public async Task<int> ResumeAsync(string queue, string owner, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Resume);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("channel", sql.Channel);
        AddWho(command.Parameters, by, null);
        return await command.ExecuteScalarAsync(cancellationToken) is int resumed ? resumed : 0;
    }

    /// <summary>Sets or replaces an operator's limit on a budget (key empty for every key) and logs it. Returns 1.</summary>
    public async Task<int> LimitBudgetAsync(
        string budget, string key, double rate, DateTimeOffset? until, string by, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.LimitBudget);
        command.Parameters.AddWithValue("budget", budget);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("rate", rate);
        command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = (object?)until?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        AddWho(command.Parameters, by, reason);
        return await command.ExecuteScalarAsync(cancellationToken) is int set ? set : 0;
    }

    /// <summary>Removes an operator's limit and logs it. Returns 1, or 0 when there was none.</summary>
    public async Task<int> ClearBudgetLimitAsync(string budget, string key, string by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.ClearBudgetLimit);
        command.Parameters.AddWithValue("budget", budget);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        AddWho(command.Parameters, by, null);
        return await command.ExecuteScalarAsync(cancellationToken) is int cleared ? cleared : 0;
    }

    public async Task<List<BudgetLimitInfo>> BudgetLimitsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.BudgetLimits);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var limits = new List<BudgetLimitInfo>();
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            limits.Add(new BudgetLimitInfo
            {
                Budget = reader.GetString(0),
                Key = reader.GetString(1) is { Length: > 0 } key ? key : null,
                Rate = reader.GetDouble(2),
                Until = Moment(reader, 3),
                By = reader.GetString(4),
                Reason = reader.GetString(5),
                SetAt = reader.GetFieldValue<DateTimeOffset>(6)
            });
        }

        return limits;
    }

    public async Task<List<PauseInfo>> PausesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Pauses);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pauses = new List<PauseInfo>();
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            pauses.Add(new PauseInfo
            {
                Queue = reader.GetString(0),
                Owner = reader.GetString(1) is { Length: > 0 } owner ? owner : null,
                By = reader.GetString(2),
                Reason = reader.GetString(3),
                PausedAt = reader.GetFieldValue<DateTimeOffset>(4),
                Until = Moment(reader, 5)
            });
        }

        return pauses;
    }

    public async Task<List<OperatorAction>> ActionsAsync(string? queue, string? owner, long? before, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Actions);
        command.Parameters.Add(Text("queue", queue));
        command.Parameters.Add(Text("owner", owner));
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Bigint) { Value = (object?)before ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actions = new List<OperatorAction>(limit);
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
        {
            actions.Add(new OperatorAction
            {
                Id = reader.GetInt64(0),
                At = reader.GetFieldValue<DateTimeOffset>(1),
                By = reader.GetString(2),
                Action = reader.GetString(3),
                Queue = Text(reader, 4),
                Owner = Text(reader, 5),
                Target = Text(reader, 6),
                Reason = Text(reader, 7),
                Changed = reader.GetInt32(8)
            });
        }

        return actions;
    }

    private static void AddWho(NpgsqlParameterCollection parameters, string by, string? reason)
    {
        parameters.AddWithValue("by", by);
        parameters.Add(Text("reason", reason));
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? Moment(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
