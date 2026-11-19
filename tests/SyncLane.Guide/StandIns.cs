using System.Collections;
using System.Linq.Expressions;

namespace GuideExamples;

// What the guide's examples take from the application: just enough of each for the examples to compile as they are
// written. A query over Shops is an IQueryable, so a lambda in it is an expression tree, as it is with EF Core.

public sealed class Shop
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public bool IsConnected { get; set; }

    public bool IsNew { get; set; }

    public string AccessToken { get; set; } = "";

    public string? RefreshToken { get; set; }
}

public sealed class AppDb
{
    public Table<Shop> Shops { get; } = new([]);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}

public sealed class Table<T>(List<T> rows) : IQueryable<T> where T : class
{
    private readonly IQueryable<T> _query = rows.AsQueryable();

    public Type ElementType => _query.ElementType;

    public Expression Expression => _query.Expression;

    public IQueryProvider Provider => _query.Provider;

    public ValueTask<T?> FindAsync(object?[]? keyValues, CancellationToken cancellationToken) => ValueTask.FromResult(rows.FirstOrDefault());

    public IEnumerator<T> GetEnumerator() => _query.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class QueryableExtensions
{
    public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken) =>
        Task.FromResult(source.ToList());
}

public sealed record Order(string ExternalId);

public sealed record OrdersResponse(
    bool IsThrottled, TimeSpan? RetryAfter, bool IsUnauthorized, IReadOnlyList<Order> Orders, string? NextCursor);

public interface IShopClient
{
    Task<OrdersResponse> GetOrdersAsync(
        string owner, DateTimeOffset? windowStart, DateTimeOffset? windowEnd, string? cursor, CancellationToken cancellationToken);
}

public sealed class OrderRepository
{
    public Task UpsertAsync(string owner, IReadOnlyList<Order> orders, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ProductHandler : IJobHandler
{
    public Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken) => Task.FromResult(new JobPage(null, 0));
}

public sealed class InvoiceHandler : IJobHandler
{
    public Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken) => Task.FromResult(new JobPage(null, 0));
}

public sealed class ExpiredDataCleanup : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

public sealed record User(string Id);

public sealed class FakeShopClient(int ordersPerPage, int pages) : IShopClient
{
    public Task<OrdersResponse> GetOrdersAsync(
        string owner, DateTimeOffset? windowStart, DateTimeOffset? windowEnd, string? cursor, CancellationToken cancellationToken)
    {
        var page = cursor is null ? 1 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var orders = Enumerable.Range(0, ordersPerPage).Select(index => new Order($"{page}-{index}")).ToList();
        return Task.FromResult(new OrdersResponse(false, null, false, orders, page < pages ? $"{page + 1}" : null));
    }
}

public static class SyncRegistration
{
    public static IServiceCollection AddShopSync(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSyncLane(configuration.GetSection("SyncLane"), lane =>
            lane.ConnectionString = configuration.GetConnectionString("Postgres")!);
}

public static class TestDatabase
{
    public static string ConnectionString => Environment.GetEnvironmentVariable("APP_TEST_DB") ?? "";
}

public static class TestShops
{
    public const int First = 1;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute;

public static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
    }
}

public abstract class EndToEndHelpers
{
    protected static async Task WaitUntilAsync(Func<Task<bool>> done, TimeSpan timeout)
    {
        using var timer = new CancellationTokenSource(timeout);

        // Sequential: polling until the condition holds.
        while (!await done())
            await Task.Delay(TimeSpan.FromSeconds(1), timer.Token);
    }

    protected static Task<int> CountOrdersAsync(int shopId) => Task.FromResult(0);

    protected static Task DropSchemaAsync(string schema) => Task.CompletedTask;
}

public static class TelemetryExtensions
{
    public static TelemetryBuilder AddOpenTelemetry(this IServiceCollection services) => new();
}

public sealed class TelemetryBuilder
{
    public TelemetryBuilder WithMetrics(Action<MetricsBuilder> configure) => this;

    public TelemetryBuilder WithTracing(Action<TracingBuilder> configure) => this;
}

public sealed class MetricsBuilder
{
    public MetricsBuilder AddMeter(params string[] names) => this;
}

public sealed class TracingBuilder
{
    public TracingBuilder AddSource(params string[] names) => this;
}
