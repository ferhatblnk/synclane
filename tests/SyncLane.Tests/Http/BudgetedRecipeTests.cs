using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

/// <summary>A recipe with a <c>rate</c> section, one page at a time, against the database that holds the buckets.</summary>
public class BudgetedRecipeTests : PostgresTest
{
    [PostgresFact]
    public async Task Every_request_waits_for_a_token_of_the_recipes_budget()
    {
        var api = new TimedApi();
        await using var source = Source(api, budget: options => options.AddBudget("shop", 10, TimeSpan.FromSeconds(1)));

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => source.RunAsync("shop-7")));

        Assert.InRange(api.Spread.TotalSeconds, 0.18, 2);
    }

    [PostgresFact]
    public async Task A_429_pauses_the_budget_for_every_page_of_the_key_and_no_other()
    {
        var api = new TimedApi(first: () =>
        {
            var response = FakeApi.Response(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return response;
        });
        await using var source = Source(api, budget: options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10));

        var throttled = await Assert.ThrowsAsync<JobThrottledException>(() => source.RunAsync("shop-7"));
        await source.RunAsync("shop-8");
        await source.RunAsync("shop-7");

        Assert.InRange(throttled.RetryAfter!.Value.TotalSeconds, 0.95, 1);
        var (throttledAt, otherOwner, sameOwner) = (api.Times[0], api.Times[1], api.Times[2]);
        Assert.InRange((otherOwner - throttledAt).TotalSeconds, 0, 0.2);
        Assert.InRange((sameOwner - throttledAt).TotalSeconds, 0.95, 3);
    }

    [PostgresFact]
    public async Task Owners_with_one_key_share_one_bucket_and_a_long_wait_postpones_the_job_before_anything_is_sent()
    {
        var api = new TimedApi();
        await using var source = Source(
            api,
            budget: options =>
            {
                options.MaxBudgetWait = TimeSpan.FromMilliseconds(100);
                options.AddBudget("shop", 1, TimeSpan.FromMinutes(1));
            },
            rate: """{ "budget": "shop", "key": "shared-account" }""");

        await source.RunAsync("shop-7");
        var postponed = await Assert.ThrowsAsync<JobPostponedException>(() => source.RunAsync("shop-8"));

        Assert.StartsWith(SyncErrorCodes.BudgetWaitTooLong, postponed.Message);
        Assert.Single(api.Times);
    }

    private BudgetedSource Source(TimedApi api, Action<SyncLaneOptions> budget, string rate = """{ "budget": "shop" }""") =>
        new(api, budget, RateTests.Recipe(rate), ConnectionString!, Schema);

    /// <summary>The handler with the engine's budgets, run one page at a time without workers.</summary>
    private sealed class BudgetedSource : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public BudgetedSource(TimedApi api, Action<SyncLaneOptions> budget, string recipe, string connectionString, string schema)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSyncLane(options =>
            {
                options.ConnectionString = connectionString;
                options.Schema = schema;
                budget(options);
                options.AddQueue("shops", queue => queue.Handle<HttpRecipeHandler>("orders"));
            });
            services.AddSyncLaneHttp(http => http.Recipe("orders", recipe).WriteTo((_, _) => Task.CompletedTask));
            services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);
            _provider = services.BuildServiceProvider();
        }

        public async Task<JobPage> RunAsync(string owner)
        {
            await using var scope = _provider.CreateAsyncScope();
            var job = new Job { Id = 1, Queue = "shops", Owner = owner, Lane = owner, Type = "orders", Mode = JobMode.Initial, Status = JobStatus.Running };
            return await scope.ServiceProvider.GetRequiredService<HttpRecipeHandler>().RunPageAsync(job, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    /// <summary>Answers with an empty page, the first time with what the test asks, and notes when each request arrived.</summary>
    private sealed class TimedApi(Func<HttpResponseMessage>? first = null) : HttpMessageHandler
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<TimeSpan> _times = new();
        private int _count;

        public IReadOnlyList<TimeSpan> Times => _times.ToArray();

        public TimeSpan Spread => Times.Count < 2 ? TimeSpan.Zero : Times.Max() - Times.Min();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _times.Enqueue(_clock.Elapsed);
            return Task.FromResult(Interlocked.Increment(ref _count) == 1 && first is not null
                ? first()
                : FakeApi.Response(HttpStatusCode.OK, """{ "items": [] }"""));
        }
    }
}
