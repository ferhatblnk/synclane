using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SyncLane.Instances;

namespace SyncLane.Storage;

/// <summary>Reads and writes the instances table. It borrows the job store's connection pool.</summary>
internal sealed class InstanceStore(NpgsqlDataSource dataSource, InstanceSql sql)
{
    /// <summary>
    /// Registers <paramref name="me"/> unless <paramref name="refuse"/>, given the live instances of the moment, returns
    /// errors. Reading and writing happen in one transaction behind a lock, so the decision cannot go stale.
    /// </summary>
    public async Task<(IReadOnlyList<InstanceRow> Live, IReadOnlyList<SyncError> Refused)> RegisterAsync(
        InstanceRow me,
        DateTimeOffset aliveSince,
        Func<IReadOnlyList<InstanceRow>, IReadOnlyList<SyncError>> refuse,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var batch = new NpgsqlBatch(connection, transaction);
        var lockCommand = new NpgsqlBatchCommand(sql.Lock);
        lockCommand.Parameters.AddWithValue("lock", sql.LockKey);
        var liveCommand = new NpgsqlBatchCommand(sql.Live);
        liveCommand.Parameters.AddWithValue("id", me.Id);
        liveCommand.Parameters.AddWithValue("alive_since", aliveSince.ToUniversalTime());
        batch.BatchCommands.Add(lockCommand);
        batch.BatchCommands.Add(liveCommand);

        var live = new List<InstanceRow>();
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken))
        {
            await reader.NextResultAsync(cancellationToken);

            // Sequential: reading rows of one data reader.
            while (await reader.ReadAsync(cancellationToken))
                live.Add(Read(reader));
        }

        var refused = refuse(live);
        if (refused.Count > 0)
            return (live, refused);

        await using (var upsert = Upsert(connection, transaction, me, me.HeartbeatAt))
            await upsert.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return (live, []);
    }

    /// <summary>Says this instance is alive and forgets instances silent since <paramref name="forgetBefore"/>, in one round trip.</summary>
    public async Task HeartbeatAsync(InstanceRow me, DateTimeOffset now, DateTimeOffset forgetBefore, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection);

        var upsert = new NpgsqlBatchCommand(sql.Upsert);
        AddValues(upsert.Parameters, me, now);
        var forget = new NpgsqlBatchCommand(sql.Forget);
        forget.Parameters.AddWithValue("forget_before", forgetBefore.ToUniversalTime());
        batch.BatchCommands.Add(upsert);
        batch.BatchCommands.Add(forget);

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Every registered instance, live or gone, by application.</summary>
    public async Task<List<InstanceRow>> AllAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.All);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<InstanceRow>();
        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Read(reader));

        return rows;
    }

    /// <summary>Marks this instance stopped. Its row stays, so the monitor shows when it stopped, until it is forgotten.</summary>
    public async Task StopAsync(string id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql.Stop);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlCommand Upsert(NpgsqlConnection connection, NpgsqlTransaction transaction, InstanceRow me, DateTimeOffset now)
    {
        var command = new NpgsqlCommand(sql.Upsert, connection, transaction);
        AddValues(command.Parameters, me, now);
        return command;
    }

    private static void AddValues(NpgsqlParameterCollection parameters, InstanceRow me, DateTimeOffset now)
    {
        parameters.AddWithValue("id", me.Id);
        parameters.AddWithValue("application", me.Application);
        parameters.AddWithValue("host", me.Host);
        parameters.AddWithValue("version", me.Version);
        parameters.Add(new NpgsqlParameter("queues", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = me.Queues.ToArray() });
        parameters.AddWithValue("budgets", WriteBudgets(me.Budgets));
        parameters.AddWithValue("started_at", me.StartedAt.ToUniversalTime());
        parameters.AddWithValue("now", now.ToUniversalTime());
    }

    private static InstanceRow Read(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetFieldValue<string[]>(4),
        ReadBudgets(reader.GetString(5)),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));

    // Written and read by hand: a few fields at start-up and every half minute, and no serializer to keep in step.
    internal static string WriteBudgets(IReadOnlyList<BudgetDescription> budgets)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var budget in budgets)
            {
                json.WriteStartObject();
                json.WriteString("name", budget.Name);
                json.WriteNumber("limit", budget.Limit);
                json.WriteNumber("per_seconds", budget.Per.TotalSeconds);
                json.WriteNumber("burst", budget.Burst);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static IReadOnlyList<BudgetDescription> ReadBudgets(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.EnumerateArray()
            .Select(budget => new BudgetDescription(
                budget.GetProperty("name").GetString() ?? "",
                budget.GetProperty("limit").GetInt32(),
                TimeSpan.FromSeconds(budget.GetProperty("per_seconds").GetDouble()),
                budget.GetProperty("burst").GetInt32()))
            .ToList();
    }
}
