using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

/// <summary>
/// The gate for rate headers: APIs that count requests in windows of one second and say on every answer what is left, in
/// the ways real APIs do. Another app on the same account spends part of every window, which no declared budget can
/// know. Once the recipe names the headers, nothing is throttled; without them, the same run is.
/// </summary>
public class HeaderGateTests : PostgresTest
{
    private const int Owners = 4;
    private const int PagesPerJob = 3;
    private const int Quota = 8;
    private const int Neighbour = 2;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(30);

    public static TheoryData<string, bool> Patterns => new()
    {
        { "seconds", true }, { "seconds", false },
        { "unix-403", true }, { "unix-403", false },
        { "ietf", true }, { "ietf", false }
    };

    [PostgresTheory]
    [MemberData(nameof(Patterns))]
    public async Task A_window_api_is_never_throttled_once_its_headers_are_declared(string pattern, bool declared)
    {
        var api = new WindowApi(pattern);
        using var host = Host(api, declared ? Headers(pattern) : "");

        if (declared)
        {
            var jobs = await RunAsync(host);

            Assert.All(jobs, job => Assert.Equal(JobStatus.Completed, job.Status));
            Assert.Equal(0, api.Spent);
            Assert.Equal(Owners * PagesPerJob, api.Served);
        }
        else
        {
            // The budget alone allows the whole quota, but the other app has spent part of it.
            await RunAsync(host, until: _ => api.Spent > 0);
            Assert.True(api.Spent > 0);
        }
    }

    private static string Headers(string pattern) => pattern switch
    {
        "seconds" => """, "headers": { "remaining": "X-Rate-Limit-Remaining", "reset": "X-Rate-Limit-Reset", "resetFormat": "seconds" }""",
        "unix-403" => """, "headers": { "remaining": "x-ratelimit-remaining", "reset": "x-ratelimit-reset", "resetFormat": "unix" }""",
        _ => """, "headers": { "remaining": "RateLimit", "remainingFormat": "ietf" }"""
    };

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

    private IHost Host(WindowApi api, string headers)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.PollInterval = TimeSpan.FromMilliseconds(20);

            // What the API's documentation says: the whole quota, at once if need be.
            options.AddBudget("shop", Quota, TimeSpan.FromSeconds(1), burst: Quota);
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
              "pagination": { "type": "page", "param": "page", "size": 2 },
              "rate": { "budget": "shop", "key": "shared-account"{{{headers}}} }
            }
            """).WriteTo((_, _) => Task.CompletedTask));
        builder.Services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);

        return builder.Build();
    }

    /// <summary>
    /// An API that allows <see cref="Quota"/> requests in each second of the clock, of which another app spends
    /// <see cref="Neighbour"/> at the start. It says what is left in the way <paramref name="pattern"/> names:
    /// <list type="bullet">
    /// <item><c>seconds</c>: remaining, and whole seconds to the next window, rounded up; a spent window answers 429 without <c>Retry-After</c>.</item>
    /// <item><c>unix-403</c>: remaining, and the next window as a Unix time, with a <c>Date</c>; a spent window answers 403.</item>
    /// <item><c>ietf</c>: the draft's <c>RateLimit</c> field; a spent window answers 429.</item>
    /// </list>
    /// Every request takes <see cref="Latency"/> to arrive. Every owner has three pages: two full ones and a short last one.
    /// </summary>
    private sealed class WindowApi(string pattern) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly ConcurrentQueue<TimeSpan> _spentAt = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _window = -1;
        private int _left;
        private int _served;

        public int Spent => _spentAt.Count;

        public int Served => _served;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // On the way there: requests of all four jobs are in flight together, as over a real network.
            await Task.Delay(Latency, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var window = now.ToUnixTimeMilliseconds() / 1000;
            int left;
            bool spent;
            lock (_lock)
            {
                if (window != _window)
                {
                    _window = window;
                    _left = Quota - Neighbour;
                }

                spent = _left <= 0;
                if (!spent)
                    _left--;

                left = _left;
            }

            HttpResponseMessage response;
            if (spent)
            {
                _spentAt.Enqueue(_clock.Elapsed);
                response = FakeApi.Response(pattern == "unix-403" ? HttpStatusCode.Forbidden : HttpStatusCode.TooManyRequests);
            }
            else
            {
                Interlocked.Increment(ref _served);
                var page = request.RequestUri!.Query.Split('=')[1];
                response = FakeApi.Response(HttpStatusCode.OK, page == PagesPerJob.ToString(CultureInfo.InvariantCulture) ? """{ "items": [1] }""" : """{ "items": [1, 2] }""");
            }

            var end = DateTimeOffset.FromUnixTimeSeconds(window + 1);
            var secondsLeft = (int)Math.Ceiling((end - now).TotalSeconds);
            switch (pattern)
            {
                case "seconds":
                    response.Headers.TryAddWithoutValidation("X-Rate-Limit-Limit", Quota.ToString(CultureInfo.InvariantCulture));
                    response.Headers.TryAddWithoutValidation("X-Rate-Limit-Remaining", left.ToString(CultureInfo.InvariantCulture));
                    response.Headers.TryAddWithoutValidation("X-Rate-Limit-Reset", secondsLeft.ToString(CultureInfo.InvariantCulture));
                    break;
                case "unix-403":
                    response.Headers.Date = now;
                    response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", left.ToString(CultureInfo.InvariantCulture));
                    response.Headers.TryAddWithoutValidation("x-ratelimit-reset", end.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    response.Headers.TryAddWithoutValidation("RateLimit", $"\"default\";r={left};t={secondsLeft}");
                    break;
            }

            return response;
        }
    }
}
