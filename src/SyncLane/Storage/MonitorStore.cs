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
                FailuresLastDay = reader.GetInt32(11)
            });
        }

        return queues;
    }

    /// <summary>A page of owners, oldest data first, with each row's sort key for the next cursor.</summary>
    public async Task<List<(OwnerFreshness Row, DateTimeOffset SortKey)>> FreshnessAsync(
        string? queue, string? owner, (DateTimeOffset Key, string Queue, string Owner, string Type)? after, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Freshness);
        command.Parameters.Add(Text("queue", queue));
        command.Parameters.Add(Text("owner", owner));
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
                Type = reader.GetString(2),
                DataUntil = completedTo ?? completedAt,
                LastCompletedAt = completedAt,
                Status = reader.IsDBNull(5) ? null : JobText.Status(reader.GetString(5)),
                Code = Text(reader, 6),
                Error = Text(reader, 7),
                NextRunAt = Moment(reader, 8),
                HistoryTotal = reader.GetInt32(9),
                HistoryCompleted = reader.GetInt32(10)
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
    public async Task<(string? Status, bool Changed)> ChangeJobAsync(string statement, long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(statement);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetInt32(1) == 1) : (null, false);
    }

    public async Task<int> RequeueByCodeAsync(string queue, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.RequeueByCode);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PrioritizeOwnerAsync(string queue, string owner, int boost, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.PrioritizeOwner);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("boost", boost);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? Moment(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
