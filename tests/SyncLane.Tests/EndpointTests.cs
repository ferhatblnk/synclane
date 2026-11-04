using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncLane.AspNetCore;
using SyncLane.Monitoring;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>The monitor over HTTP: safe until the application says who may use it.</summary>
public class EndpointTests : PostgresTest
{
    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.7");

    [PostgresFact]
    public async Task Without_authorization_only_this_machine_is_answered_and_a_proxy_does_not_count()
    {
        await using var app = await StartAsync();

        var local = await SendAsync(app, "GET", "/synclane/overview", IPAddress.Loopback);
        var remote = await SendAsync(app, "GET", "/synclane/overview", Remote);
        var proxied = await SendAsync(app, "GET", "/synclane/overview", IPAddress.Loopback, ("X-Forwarded-For", "198.51.100.4"));
        var unknown = await SendAsync(app, "GET", "/synclane/overview", null);

        Assert.Equal(200, local.Status);
        Assert.Equal((403, 403, 403), (remote.Status, proxied.Status, unknown.Status));
        Assert.Contains(EndpointErrorCodes.RemoteWithoutAuthorization, remote.Body);
    }

    [PostgresFact]
    public async Task With_authorization_a_remote_request_is_answered_once_the_user_is_signed_in()
    {
        await using var app = await StartAsync(authorize: true);

        var anonymous = await SendAsync(app, "GET", "/synclane/overview", Remote);
        var signedIn = await SendAsync(app, "GET", "/synclane/overview", Remote, (TestAuth.Header, "operator"));

        Assert.Equal((401, 200), (anonymous.Status, signedIn.Status));
    }

    [PostgresFact]
    public async Task Answers_are_never_cached_and_never_show_a_job_s_cursor()
    {
        var cursor = $"page-token-{Guid.NewGuid():N}";
        var id = await InsertRowAsync("a", JobMode.Recurring, JobStatus.Failed, Noon, key: "1");
        await ExecuteAsync($"UPDATE {Schema}.jobs SET cursor = '{cursor}' WHERE id = {id}");
        await using var app = await StartAsync();

        var jobs = await SendAsync(app, "GET", $"/synclane/jobs?queue={Queue}", IPAddress.Loopback);

        Assert.Equal(200, jobs.Status);
        Assert.Contains($"\"id\":{id}", jobs.Body);
        Assert.Contains("\"status\":\"failed\"", jobs.Body);
        Assert.DoesNotContain(cursor, jobs.Body);
        Assert.Equal(("no-store", "nosniff"), (jobs.Headers.CacheControl.ToString(), jobs.Headers.XContentTypeOptions.ToString()));
    }

    [PostgresFact]
    public async Task A_repair_needs_the_action_header_and_read_only_maps_none()
    {
        var id = await InsertRowAsync("a", JobMode.Recurring, JobStatus.Abandoned, Noon, key: "1", completedAt: Noon);
        await using (var readOnly = await StartAsync(options => options.ReadOnly = true))
        {
            var refused = await SendAsync(readOnly, "POST", $"/synclane/jobs/{id}/requeue", IPAddress.Loopback, (SyncLaneEndpointOptions.ActionHeader, "1"));
            Assert.NotEqual(200, refused.Status);
        }

        await using var app = await StartAsync();
        var withoutHeader = await SendAsync(app, "POST", $"/synclane/jobs/{id}/requeue", IPAddress.Loopback);
        var stillAbandoned = (await JobAsync(id)).Status;
        var requeued = await SendAsync(app, "POST", $"/synclane/jobs/{id}/requeue", IPAddress.Loopback, (SyncLaneEndpointOptions.ActionHeader, "1"));

        Assert.Equal((400, JobStatus.Abandoned), (withoutHeader.Status, stillAbandoned));
        Assert.Contains(EndpointErrorCodes.ActionHeaderMissing, withoutHeader.Body);
        Assert.Equal((200, "1"), (requeued.Status, requeued.Body));
        Assert.Equal(JobStatus.Pending, (await JobAsync(id)).Status);
    }

    [PostgresFact]
    public async Task A_refusal_comes_back_as_problem_details_with_its_code_and_status()
    {
        await using var app = await StartAsync();

        var missing = await SendAsync(app, "POST", "/synclane/jobs/999999/restart", IPAddress.Loopback, (SyncLaneEndpointOptions.ActionHeader, "1"));
        var badLimit = await SendAsync(app, "GET", "/synclane/freshness?limit=0", IPAddress.Loopback);

        Assert.Equal(404, missing.Status);
        Assert.Contains("\"title\":\"SL3008\"", missing.Body);
        Assert.Equal(400, badLimit.Status);
        Assert.Contains("\"code\":\"SL3003\"", badLimit.Body);
    }

    [PostgresFact]
    public async Task Every_endpoint_is_listed_for_openapi_under_its_tag_with_what_it_returns()
    {
        await using var app = await StartAsync();

        var descriptions = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Where(description => description.RelativePath!.StartsWith("synclane/", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(12, descriptions.Count);
        Assert.All(descriptions, description => Assert.Contains(
            description.ActionDescriptor.EndpointMetadata.OfType<ITagsMetadata>(), tags => tags.Tags.Contains("SyncLane")));
        var overview = Assert.Single(descriptions, description => description.RelativePath == "synclane/overview");
        Assert.Contains(overview.SupportedResponseTypes, response => response.Type == typeof(IReadOnlyList<QueueOverview>));
    }

    [PostgresFact]
    public async Task The_stream_sends_one_job_event_per_change()
    {
        await using var app = await StartAsync(authorize: true);
        using var client = app.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add(TestAuth.Header, "operator");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync("/synclane/stream", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var ready = await ReadEventAsync(reader, timeout.Token);
        await app.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(timeout.Token);
        await EnqueueAsync(Noon, Planned("a", key: "streamed"));
        var job = await ReadEventAsync(reader, timeout.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("event: ready", ready[0]);
        Assert.Equal("event: job", job[0]);
        Assert.Contains("\"owner\":\"a\"", job[1]);
        Assert.Contains("\"status\":\"pending\"", job[1]);
    }

    /// <summary>The lines of the next event, up to the blank line that ends it.</summary>
    private static async Task<List<string>> ReadEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        // Sequential: reading one event line by line.
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 && lines.Count > 0)
                break;
            if (line.Length > 0)
                lines.Add(line);
        }

        return lines;
    }

    private async Task<WebApplication> StartAsync(Action<SyncLaneEndpointOptions>? configure = null, bool authorize = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.Application = "endpoint-tests";
            options.RunPlanner = false;
            options.RunWorkers = false;
            options.AddQueue(Queue, queue => queue.Handle<ProductHandler>("orders"));
        });
        builder.Services.AddEndpointsApiExplorer();
        if (authorize)
        {
            builder.Services.AddAuthentication(TestAuth.Name).AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.Name, null);
            builder.Services.AddAuthorization();
        }

        var app = builder.Build();
        if (authorize)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        var group = app.MapSyncLane(configure: configure);
        if (authorize)
            group.RequireAuthorization();

        await app.StartAsync();
        return app;
    }

    private static async Task<(int Status, string Body, IHeaderDictionary Headers)> SendAsync(
        WebApplication app, string method, string pathAndQuery, IPAddress? remote, params (string Name, string Value)[] headers)
    {
        var context = await app.GetTestServer().SendAsync(http =>
        {
            http.Request.Method = method;
            var query = pathAndQuery.IndexOf('?');
            http.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
            http.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery[query..]);
            http.Connection.RemoteIpAddress = remote;
            foreach (var (name, value) in headers)
                http.Request.Headers[name] = value;
        });

        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(), context.Response.Headers);
    }

    /// <summary>Signs in whoever sends the header, under that name. Nothing else is accepted.</summary>
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Test";
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
