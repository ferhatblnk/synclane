using Npgsql;
using SyncLane.Planning;
using SyncLane.Storage;

namespace SyncLane.Tests.Support;

/// <summary>Gives every test a schema of its own and drops it afterwards.</summary>
public abstract class PostgresTest : IAsyncLifetime
{
    public const string Variable = "SYNCLANE_TEST_DB";

    protected const string Queue = "stores";

    protected static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource? _admin;

    /// <summary>
    /// The test database, with a small pool per data source. Test classes run side by side and each opens pools of its
    /// own; uncapped, a busy moment went past PostgreSQL's default of 100 connections and random tests failed with
    /// "too many clients". A test that needs more connections than its pool holds waits for one instead.
    /// </summary>
    public static string? ConnectionString { get; } =
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value
            ? new NpgsqlConnectionStringBuilder(value) { MaxPoolSize = MaxPoolSize }.ConnectionString
            : null;

    private const int MaxPoolSize = 4;

    protected string Schema { get; } = $"t_{Guid.NewGuid():N}";

    internal JobStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _admin = NpgsqlDataSource.Create(ConnectionString!);
        Store = new JobStore(NpgsqlDataSource.Create(ConnectionString!), new JobSql(Schema));
        await Store.MigrateAsync(CancellationToken.None);
    }

    public virtual async Task DisposeAsync()
    {
        await Store.DisposeAsync();
        await ExecuteAsync($"DROP SCHEMA IF EXISTS {Schema} CASCADE");
        await _admin!.DisposeAsync();
    }

    internal static PlannedJob Planned(
        string owner,
        string type = "orders",
        string? lane = null,
        int priority = 0,
        string? key = null,
        JobMode mode = JobMode.Initial,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null) =>
        new(owner, lane ?? owner, type, mode, priority, windowStart, windowEnd, key);

    internal Task<int> EnqueueAsync(DateTimeOffset now, params PlannedJob[] jobs) =>
        Store.EnqueueAsync(Queue, jobs, now, CancellationToken.None);

    internal Task<Job?> LeaseAsync(string worker, DateTimeOffset now, TimeSpan? lease = null) =>
        Store.LeaseAsync(Queue, worker, now, now + (lease ?? TimeSpan.FromMinutes(5)), CancellationToken.None);

    internal async Task<IReadOnlyList<Job>> JobsAsync(JobStatus? status = null) =>
        (await Store.GetJobsAsync(new JobQuery { Queue = Queue, Status = status }, null, JobQuery.MaxLimit, false, CancellationToken.None)).Jobs
        .OrderBy(job => job.Id)
        .ToList();

    internal async Task<Job> JobAsync(long id) =>
        await Store.FindAsync(id, CancellationToken.None) ?? throw new InvalidOperationException($"Job {id} does not exist.");

    /// <summary>Every row of every table in the test's schema, as JSON text, for tests that search what was stored.</summary>
    protected async Task<string> DumpSchemaAsync()
    {
        await using var command = _admin!.CreateCommand(
            $"SELECT coalesce(string_agg(row_to_json(j)::text, E'\\n'), '') FROM {Schema}.jobs j " +
            $"UNION ALL SELECT coalesce(string_agg(row_to_json(c)::text, E'\\n'), '') FROM {Schema}.coverage c " +
            $"UNION ALL SELECT coalesce(string_agg(row_to_json(i)::text, E'\\n'), '') FROM {Schema}.instances i " +
            $"UNION ALL SELECT coalesce(string_agg(row_to_json(m)::text, E'\\n'), '') FROM {Schema}.migrations m " +
            $"UNION ALL SELECT coalesce(string_agg(row_to_json(b)::text, E'\\n'), '') FROM {Schema}.rate_buckets b");

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        // Sequential: reading rows from one data reader.
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));

        return string.Join('\n', rows);
    }

    protected async Task<int> ExecuteAsync(string statement)
    {
        await using var command = _admin!.CreateCommand(statement);
        return await command.ExecuteNonQueryAsync();
    }

    protected async Task<T?> ScalarAsync<T>(string statement)
    {
        await using var command = _admin!.CreateCommand(statement);
        return await command.ExecuteScalarAsync() is T value ? value : default;
    }

    /// <summary>
    /// Writes a job row straight into the table, the way an older version or another system would: the summary the
    /// planner reads is not touched.
    /// </summary>
    protected async Task<long> InsertRowAsync(
        string owner,
        JobMode mode,
        JobStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null,
        string? key = null,
        DateTimeOffset? completedAt = null,
        string queue = Queue,
        string type = "orders")
    {
        await using var command = _admin!.CreateCommand(
            $"""
            INSERT INTO {Schema}.jobs
                (queue, owner, lane, type, mode, status, window_start, window_end, dedup_key, next_run_at, created_at, completed_at, updated_at)
            VALUES (@queue, @owner, @owner, @type, @mode, @status, @window_start, @window_end, @key, @created_at, @created_at, @completed_at, @created_at)
            RETURNING id
            """);
        command.Parameters.AddWithValue("queue", queue);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("mode", JobText.Of(mode));
        command.Parameters.AddWithValue("status", JobText.Of(status));
        command.Parameters.Add(new NpgsqlParameter("window_start", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)windowStart ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("window_end", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)windowEnd ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)key ?? DBNull.Value });
        command.Parameters.AddWithValue("created_at", createdAt);
        command.Parameters.Add(new NpgsqlParameter("completed_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)completedAt ?? DBNull.Value });
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
