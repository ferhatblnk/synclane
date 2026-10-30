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

    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;

    protected string Schema { get; } = $"t_{Guid.NewGuid():N}";

    internal JobStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _admin = NpgsqlDataSource.Create(ConnectionString!);
        Store = new JobStore(NpgsqlDataSource.Create(ConnectionString!), new JobSql(Schema));
        await Store.MigrateAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
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
            $"UNION ALL SELECT coalesce(string_agg(row_to_json(m)::text, E'\\n'), '') FROM {Schema}.migrations m");

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
}
