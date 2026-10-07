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
        await Store.EnsureSchemaAsync(CancellationToken.None);
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
        (await Store.GetJobsAsync(new JobQuery { Queue = Queue, Status = status, Limit = JobQuery.MaxLimit }, CancellationToken.None))
        .OrderBy(job => job.Id)
        .ToList();

    internal async Task<Job> JobAsync(long id) =>
        await Store.FindAsync(id, CancellationToken.None) ?? throw new InvalidOperationException($"Job {id} does not exist.");

    protected async Task<int> ExecuteAsync(string statement)
    {
        await using var command = _admin!.CreateCommand(statement);
        return await command.ExecuteNonQueryAsync();
    }
}
