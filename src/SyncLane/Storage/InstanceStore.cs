using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SyncLane.Instances;

namespace SyncLane.Storage;

/// <summary>Reads and writes the instances table. It borrows the job store's connection pool.</summary>
internal sealed class InstanceStore(NpgsqlDataSource dataSource, InstanceSql sql)
{
    /// <summary>
    /// Writes the row <paramref name="decide"/> makes from the live instances of the moment and this application's
    /// switch, and returns it with the errors it gave. When it gives no row, nothing is written. Reading and writing
    /// happen in one transaction behind a lock, so the decision cannot go stale.
    /// </summary>
    public async Task<(IReadOnlyList<InstanceRow> Live, InstanceRow? Written, IReadOnlyList<SyncError> Refused)> RegisterAsync(
        InstanceIdentity me,
        DateTimeOffset aliveSince,
        Func<IReadOnlyList<InstanceRow>, SwitchRow?, (InstanceRow? Row, IReadOnlyList<SyncError> Refused)> decide,
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
        batch.BatchCommands.Add(SwitchCommand(me));

        var live = new List<InstanceRow>();
        SwitchRow? set;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken))
        {
            await reader.NextResultAsync(cancellationToken);

            // Sequential: reading rows of one data reader.
            while (await reader.ReadAsync(cancellationToken))
                live.Add(Read(reader));

            await reader.NextResultAsync(cancellationToken);
            set = await ReadSwitchAsync(reader, cancellationToken);
        }

        var (row, refused) = decide(live, set);
        if (row is null)
            return (live, null, refused);

        await using (var upsert = Upsert(connection, transaction, row, row.HeartbeatAt))
            await upsert.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return (live, row, refused);
    }

    /// <summary>
    /// Says this instance is alive, forgets instances silent since <paramref name="forgetBefore"/>, and reads this
    /// application's switch and its other instances alive since <paramref name="aliveSince"/>, in one round trip.
    /// </summary>
    public async Task<(SwitchRow? Set, IReadOnlyList<InstanceRow> Namesakes)> HeartbeatAsync(
        InstanceIdentity identity, InstanceRow me, DateTimeOffset now, DateTimeOffset aliveSince, DateTimeOffset forgetBefore,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection);

        var namesakes = new NpgsqlBatchCommand(sql.Namesakes);
        namesakes.Parameters.AddWithValue("application", identity.Application);
        namesakes.Parameters.AddWithValue("id", identity.Id);
        namesakes.Parameters.AddWithValue("alive_since", aliveSince.ToUniversalTime());
        var upsert = new NpgsqlBatchCommand(sql.Upsert);
        AddValues(upsert.Parameters, me, now);
        var forget = new NpgsqlBatchCommand(sql.Forget);
        forget.Parameters.AddWithValue("forget_before", forgetBefore.ToUniversalTime());
        batch.BatchCommands.Add(SwitchCommand(identity));
        batch.BatchCommands.Add(namesakes);
        batch.BatchCommands.Add(upsert);
        batch.BatchCommands.Add(forget);

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var set = await ReadSwitchAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var others = new List<InstanceRow>();

        // Sequential: reading rows of one data reader.
        while (await reader.ReadAsync(cancellationToken))
            others.Add(Read(reader));

        // Sequential: the remaining results of one batch, so an error in the upsert or the clean-up is not lost.
        while (await reader.NextResultAsync(cancellationToken))
        {
        }

        return (set, others);
    }

    private NpgsqlBatchCommand SwitchCommand(InstanceIdentity me)
    {
        var command = new NpgsqlBatchCommand(sql.Switch);
        command.Parameters.AddWithValue("application", me.Application);
        command.Parameters.AddWithValue("role", me.Role);
        return command;
    }

    private static async Task<SwitchRow?> ReadSwitchAsync(NpgsqlDataReader reader, CancellationToken cancellationToken) =>
        await reader.ReadAsync(cancellationToken)
            ? new SwitchRow(Flag(reader, 0), Flag(reader, 1), Flag(reader, 2))
            : null;

    private static bool? Flag(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

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
        parameters.AddWithValue("role", me.Role);
        parameters.Add(Flag("runs_workers", me.RunsWorkers));
        parameters.Add(Flag("runs_planner", me.RunsPlanner));
        parameters.Add(Flag("listens", me.Listens));
        parameters.Add(new NpgsqlParameter("declares", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = me.Declares is { } declares ? declares.ToArray() : DBNull.Value
        });
    }

    private static NpgsqlParameter Flag(string name, bool? value) =>
        new(name, NpgsqlDbType.Boolean) { Value = value is { } flag ? flag : DBNull.Value };

    private static InstanceRow Read(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetFieldValue<string[]>(4),
        ReadBudgets(reader.GetString(5)),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetBoolean(10),
        reader.IsDBNull(11) ? null : reader.GetBoolean(11),
        reader.IsDBNull(12) ? null : reader.GetBoolean(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<string[]>(13));

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
