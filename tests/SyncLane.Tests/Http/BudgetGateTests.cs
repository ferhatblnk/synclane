using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

/// <summary>
/// The gate of part 2.1: many jobs that share one account, through the real engine, against an API with a limit of its
/// own. With a budget inside that limit nothing is throttled; after a <c>429</c> nobody on the account sends until the
/// pause ends.
/// </summary>
public class BudgetGateTests : PostgresTest
{
    private const int Owners = 8;
    private const int PagesPerJob = 4;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Jobs_sharing_a_budget_inside_the_apis_limit_are_never_throttled(bool budgeted)
    {
        // The API allows 40 a second with bursts of 4, and sends no Retry-After: the hardest kind to be polite to.
        var api = new LimitedApi(perSecond: 40, burst: 4);
        using var host = Host(api, budgeted ? """, "rate": { "budget": "shop", "key": "shared-account" }""" : "");

        if (budgeted)
        {
            var jobs = await RunAsync(host);

            Assert.All(jobs, job => Assert.Equal(JobStatus.Completed, job.Status));
            Assert.Equal(0, api.Throttled);
            Assert.Equal(Owners * PagesPerJob, api.Served);
        }
        else
        {
            // Without the budget the same run collides with the limit, so the assertion above means something.
            await RunAsync(host, until: _ => api.Throttled > 0);
            Assert.True(api.Throttled > 0);
        }
    }

    [PostgresFact]
    public async Task After_a_429_no_job_on_the_account_sends_before_the_pause_ends()
    {
        var api = new LimitedApi(perSecond: 1000, burst: 1000, throttleRequest: 6, retryAfter: "1");
        using var host = Host(api, """, "rate": { "budget": "shop", "key": "shared-account" }""");

        var jobs = await RunAsync(host);

        Assert.All(jobs, job => Assert.Equal(JobStatus.Completed, job.Status));
        var throttledAt = Assert.Single(api.ThrottledAt);

        // A request already on its way when the 429 was answered may arrive just after it; none may arrive later in the pause.
        var duringPause = api.Arrivals.Where(at => at > throttledAt + TimeSpan.FromMilliseconds(50) && at < throttledAt + TimeSpan.FromSeconds(0.95));
        Assert.Empty(duringPause);
        Assert.Contains(api.Arrivals, at => at >= throttledAt + TimeSpan.FromSeconds(0.95));
    }

    private async Task<IReadOnlyList<Job>> RunAsync(IHost host, Func<IReadOnlyList<Job>, bool>? until = null)
    {
        until ??= jobs => jobs.Count == Owners && jobs.All(job => job.Status is JobStatus.Completed or JobStatus.Abandoned);
        await host.StartAsync();
        var client = host.Services.GetRequiredService<ISyncLane>();
        var enqueued = await Task.WhenAll(Enumerable.Range(1, Owners).Select(owner =>
            client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner($"shop-{owner}"), Type = "items" })));
        Assert.All(enqueued, result => Assert.True(result.Succeeded));

        using var timeout = new CancellationTokenSource(Patience);

        // Sequential: polling until the run has reached what the test waits for.
        while (true)
        {
            var jobs = await JobsAsync();
            if (until(jobs))
            {
                await host.StopAsync();
                return jobs;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private IHost Host(LimitedApi api, string rate)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.PollInterval = TimeSpan.FromMilliseconds(20);

            // Inside the API's limit: a steady 30 a second, one at a time.
            options.AddBudget("shop", 30, TimeSpan.FromSeconds(1));
            options.AddQueue(Queue, queue =>
            {
                queue.Concurrency = Owners;
                queue.MaxAttempts = 50;
                queue.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
                queue.ThrottleBaseDelay = TimeSpan.FromMilliseconds(50);
                queue.Handle<HttpRecipeHandler>("items");
            });
        });
        builder.Services.AddSyncLaneHttp(http => http.Recipe("items", $$$"""
            {
              "name": "test/items",
              "request": { "url": "https://api.example.com/{{owner.key}}/items" },
              "records": "$.items",
              "pagination": { "type": "page", "param": "page", "size": 2 }
              {{{rate}}}
            }
            """).WriteTo((_, _) => Task.CompletedTask));
        builder.Services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);

        return builder.Build();
    }

    /// <summary>
    /// An API with a token bucket of its own, which SyncLane is not told about. Every owner has four pages: three full
    /// ones and a short last one.
    /// </summary>
    private sealed class LimitedApi(double perSecond, double burst, int throttleRequest = 0, string? retryAfter = null) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<TimeSpan> _arrivals = new();
        private readonly ConcurrentQueue<TimeSpan> _throttledAt = new();
        private readonly double _burst = burst;
        private double _tokens = burst;
        private TimeSpan _refilledAt;
        private int _requests;
        private int _served;

        public int Served => _served;

        public int Throttled => _throttledAt.Count;

        public IReadOnlyList<TimeSpan> Arrivals => _arrivals.ToArray();

        public IReadOnlyList<TimeSpan> ThrottledAt => _throttledAt.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = _clock.Elapsed;
            _arrivals.Enqueue(now);

            if (Interlocked.Increment(ref _requests) == throttleRequest || !TryTake(now))
            {
                _throttledAt.Enqueue(now);
                var throttled = FakeApi.Response(HttpStatusCode.TooManyRequests);
                if (retryAfter is not null)
                    throttled.Headers.TryAddWithoutValidation("Retry-After", retryAfter);

                return Task.FromResult(throttled);
            }

            Interlocked.Increment(ref _served);
            var page = request.RequestUri!.Query.Split('=')[1];
            var body = page == PagesPerJob.ToString() ? """{ "items": [1] }""" : """{ "items": [1, 2] }""";
            return Task.FromResult(FakeApi.Response(HttpStatusCode.OK, body));
        }

        private bool TryTake(TimeSpan now)
        {
            lock (_lock)
            {
                _tokens = Math.Min(_burst, _tokens + (now - _refilledAt).TotalSeconds * perSecond);
                _refilledAt = now;
                if (_tokens < 1)
                    return false;

                _tokens--;
                return true;
            }
        }
    }
}
