using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;

namespace SyncLane.Tests.Http;

public class RateTests
{
    [Theory]
    [InlineData("""{ "budget": "shop" }""")]
    [InlineData("""{ "budget": "shop", "key": "shared-account" }""")]
    [InlineData("""{ "budget": "shop/eu", "key": "eu-{{owner.key}}" }""")]
    public void A_rate_section_is_read_from_json(string rate)
    {
        var result = HttpRecipe.Parse(Recipe(rate));

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
    }

    [Theory]
    [InlineData("""{ "budget": "two words" }""", "rate.budget must be 1 to 100")]
    [InlineData("""{ "budget": "shop", "key": "{{window.start}}" }""", "rate.key may only use {{owner.key}}")]
    [InlineData("""{ "budget": "shop", "key": "{{owner.key" }""", "rate.key must be text")]
    [InlineData("""{ "budget": "shop", "key": " " }""", "rate.key must be text")]
    public void A_rate_section_that_cannot_work_is_refused_with_the_field_named(string rate, string expected)
    {
        var error = Assert.Single(HttpRecipe.Parse(Recipe(rate)).Errors);

        Assert.Equal(HttpErrorCodes.RateInvalid, error.Code);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task A_recipe_that_draws_from_an_undeclared_budget_stops_the_host_at_start_up()
    {
        var options = new SyncLaneOptions().AddQueue("shops", queue => queue.Handle<HttpRecipeHandler>("orders"));
        var http = new HttpSourceOptions();
        http.Recipe("orders", Recipe("""{ "budget": "shop" }""")).WriteTo((_, _) => Task.CompletedTask);

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() =>
            new RecipeCheck([options], new RecipeRegistry(http.Bindings)).StartingAsync(CancellationToken.None));

        Assert.Equal(HttpErrorCodes.BudgetMissing, thrown.Error.Code);
    }

    [Fact]
    public async Task Every_request_waits_for_a_token_of_the_recipes_budget()
    {
        var api = new TimedApi();
        await using var source = Source(api, budget: options => options.AddBudget("shop", 10, TimeSpan.FromSeconds(1)));

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => source.RunAsync("shop-7")));

        Assert.InRange(api.Spread.TotalSeconds, 0.18, 2);
    }

    [Fact]
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

        Assert.Equal(TimeSpan.FromSeconds(1), throttled.RetryAfter);
        var (throttledAt, otherOwner, sameOwner) = (api.Times[0], api.Times[1], api.Times[2]);
        Assert.InRange((otherOwner - throttledAt).TotalSeconds, 0, 0.2);
        Assert.InRange((sameOwner - throttledAt).TotalSeconds, 0.95, 3);
    }

    [Fact]
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

    private static string Recipe(string rate) =>
        $$"""{ "name": "test/orders", "request": { "url": "https://api.example.com/orders" }, "records": "$.items", "rate": {{rate}} }""";

    private static BudgetedSource Source(TimedApi api, Action<SyncLaneOptions> budget, string rate = """{ "budget": "shop" }""") =>
        new(api, budget, Recipe(rate));

    /// <summary>The handler with the engine's budgets, without a database: nothing here touches one.</summary>
    private sealed class BudgetedSource : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public BudgetedSource(TimedApi api, Action<SyncLaneOptions> budget, string recipe)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSyncLane(options =>
            {
                options.ConnectionString = "Host=unused";
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
