using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

public class EndToEndTests : PostgresTest
{
    private const string Orders =
        """
        { "name": "test/orders", "request": { "url": "https://api.example.com/shops/{{owner.key}}/orders" }, "records": "$.items" }
        """;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [PostgresFact]
    public async Task A_recipe_job_runs_through_the_engine_and_completes()
    {
        var written = new ConcurrentQueue<string>();
        var api = FakeApi.Json("""{ "items": [{ "sku": "a" }, { "sku": "b" }, { "sku": "c" }] }""");

        using var host = Host(api, (page, _) =>
        {
            foreach (var record in page.Records.EnumerateArray())
                written.Enqueue($"{page.Job.Owner}:{record.GetProperty("sku").GetString()}");

            return Task.CompletedTask;
        });

        await host.StartAsync();
        var enqueued = await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("shop-7"), Type = "orders" });
        var job = await WaitAsync(found => found.Status == JobStatus.Completed);
        await host.StopAsync();

        Assert.True(enqueued.Succeeded);
        Assert.Equal((3L, 1), (job.ProcessedCount, job.AttemptCount));
        Assert.Equal(["shop-7:a", "shop-7:b", "shop-7:c"], written);
        Assert.Equal("https://api.example.com/shops/shop-7/orders", Assert.Single(api.Requests).RequestUri!.AbsoluteUri);
    }

    [PostgresFact]
    public async Task Revoked_access_abandons_the_job_with_its_code_in_the_error_column()
    {
        using var host = Host(FakeApi.Json("{}", HttpStatusCode.Unauthorized), (_, _) => Task.CompletedTask);

        await host.StartAsync();
        await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("shop-7"), Type = "orders" });
        var job = await WaitAsync(found => found.Status == JobStatus.Abandoned);
        await host.StopAsync();

        Assert.Equal(1, job.AttemptCount);
        Assert.StartsWith($"JobAbandonedException: {HttpErrorCodes.Unauthorized}", job.Error);
    }

    [PostgresFact]
    public async Task A_paged_job_that_fails_halfway_continues_from_the_page_it_reached()
    {
        const string paged =
            """
            {
              "name": "test/items",
              "request": { "url": "https://api.example.com/items" },
              "records": "$.items",
              "pagination": { "type": "page", "param": "page", "size": 2 }
            }
            """;
        var pages = new Dictionary<string, string>
        {
            ["1"] = """{ "items": ["a", "b"] }""",
            ["2"] = """{ "items": ["c", "d"] }""",
            ["3"] = """{ "items": ["e"] }"""
        };
        var failedOnce = false;
        var api = new FakeApi((request, _) =>
        {
            var page = request.RequestUri!.Query.Split('=')[1];
            if (page == "2" && !failedOnce)
            {
                failedOnce = true;
                return Task.FromResult(FakeApi.Response(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(FakeApi.Response(HttpStatusCode.OK, pages[page]));
        });
        var written = new ConcurrentQueue<string>();

        using var host = Host(api, (page, _) =>
        {
            foreach (var record in page.Records.EnumerateArray())
                written.Enqueue(record.GetString()!);

            return Task.CompletedTask;
        }, paged);

        await host.StartAsync();
        await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("shop-7"), Type = "orders" });
        var job = await WaitAsync(found => found.Status == JobStatus.Completed);
        await host.StopAsync();

        Assert.Equal(["page=1", "page=2", "page=2", "page=3"], api.Requests.Select(request => request.RequestUri!.Query.TrimStart('?')));
        Assert.Equal(["a", "b", "c", "d", "e"], written);
        // The second run saved pages, so it is the job's first attempt since its last progress.
        Assert.Equal((5L, 1), (job.ProcessedCount, job.AttemptCount));
    }

    private IHost Host(FakeApi api, Func<RecordPage, CancellationToken, Task> sink, string recipe = Orders)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();

        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.PollInterval = TimeSpan.FromMilliseconds(50);
            options.AddQueue(Queue, queue =>
            {
                queue.Handle<HttpRecipeHandler>("orders");
                queue.RetryBaseDelay = TimeSpan.FromMilliseconds(100);
            });
        });
        builder.Services.AddSyncLaneHttp(http => http.Recipe("orders", recipe).WriteTo(sink));
        builder.Services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);

        return builder.Build();
    }

    private async Task<Job> WaitAsync(Func<Job, bool> done)
    {
        using var timeout = new CancellationTokenSource(Patience);

        // Sequential: polling the table until the job reaches the state the test waits for.
        while (true)
        {
            if ((await JobsAsync()).FirstOrDefault(done) is { } job)
                return job;

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }
}
