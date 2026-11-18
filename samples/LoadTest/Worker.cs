using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SyncLane;

namespace LoadTest;

/// <summary>One SyncLane instance of the run: every instance plans, takes jobs and calls the pretend API.</summary>
internal static class Worker
{
    public const string Queue = "shops";
    public const string Type = "orders";
    public const string Budget = "shop";

    public static async Task<int> RunAsync(Settings settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
        builder.Services.AddSingleton<PageLog>();
        builder.Services.AddHttpClient<PretendApiClient>(client => client.BaseAddress = new Uri(settings.ApiUrl));

        builder.Services.AddSyncLane(lane =>
        {
            lane.ConnectionString = settings.ConnectionString;
            lane.Application = "loadtest";
            lane.PollInterval = TimeSpan.FromSeconds(1);
            lane.AddBudget(Budget, limit: settings.BudgetLimit, per: TimeSpan.FromSeconds(1));

            lane.AddQueue(Queue, queue =>
            {
                queue.Concurrency = settings.Slots;
                queue.RetryBaseDelay = TimeSpan.FromSeconds(1);
                queue.RetryMaxDelay = TimeSpan.FromSeconds(15);
                queue.Owners<Accounts>();
                queue.Windowed<OrdersHandler>(Type,
                    horizon: TimeSpan.FromDays(settings.HorizonDays),
                    slice: TimeSpan.FromDays(settings.SliceDays));
            });
        });

        await builder.Build().RunAsync();
        return 0;
    }
}

/// <summary>Every account of the run, each in a lane of its own.</summary>
internal sealed class Accounts(Settings settings) : IOwnerSource
{
    public Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobOwner>>(
            Enumerable.Range(1, settings.Owners).Select(n => new JobOwner(Settings.Account(n))).ToList());
}

/// <summary>Reads one page of an account's orders, at the budget's pace, and logs it.</summary>
internal sealed class OrdersHandler(PretendApiClient api, RateBudgets budgets, PageLog log) : IJobHandler
{
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        var page = int.TryParse(job.Cursor, out var cursor) ? cursor : 0;
        var permit = await budgets.WaitAsync(Worker.Budget, job.Owner, cancellationToken);
        using var response = await api.GetOrdersAsync(job.Owner, page, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = await budgets.ThrottledAsync(permit, response.Headers.RetryAfter?.Delta, cancellationToken);
            throw new JobPostponedException("The pretend API answered 429.", wait);
        }

        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<PageAnswer>(cancellationToken)
            ?? throw new InvalidOperationException("The pretend API answered an empty page.");

        await log.WriteAsync(job.Owner, job.WindowStart!.Value, page, cancellationToken);
        return new JobPage(answer.Next?.ToString(System.Globalization.CultureInfo.InvariantCulture), answer.Items);
    }
}

internal sealed class PretendApiClient(HttpClient http)
{
    public Task<HttpResponseMessage> GetOrdersAsync(string account, int page, CancellationToken cancellationToken) =>
        http.GetAsync($"/accounts/{account}/orders?page={page}", cancellationToken);
}

/// <summary>
/// The destination: one row per page, its count of writes raised when the same page is written again. A missing row
/// is a lost page; a count above one is a page written twice.
/// </summary>
internal sealed class PageLog(NpgsqlDataSource db)
{
    private static readonly string Instance = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task WriteAsync(string owner, DateTimeOffset windowStart, int page, CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand(
            """
            INSERT INTO loadtest.pages (owner, window_start, page, instance, written_at)
            VALUES (@owner, @window_start, @page, @instance, now())
            ON CONFLICT (owner, window_start, page) DO UPDATE SET writes = loadtest.pages.writes + 1
            """);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("window_start", windowStart.ToUniversalTime());
        command.Parameters.AddWithValue("page", page);
        command.Parameters.AddWithValue("instance", Instance);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
