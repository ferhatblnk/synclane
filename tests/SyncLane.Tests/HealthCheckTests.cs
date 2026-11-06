using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncLane.Instances;
using SyncLane.Monitoring;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>What <see cref="SyncLaneHealthCheck"/> reports, and that it never gives a connection string away.</summary>
public class HealthCheckTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);
    private readonly HealthSignals _signals = new();

    [PostgresFact]
    public async Task A_running_host_is_healthy_through_the_health_check_service()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.ListenConnectionString = ListenConnectionString;
            options.Schema = Schema;
            options.AddQueue(Queue, queue => queue.Handle<ProductHandler>("orders"));
        });
        builder.Services.AddHealthChecks().AddCheck<SyncLaneHealthCheck>("synclane");
        using var host = builder.Build();
        await host.StartAsync();
        await host.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(CancellationToken.None);

        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        await host.StopAsync();
        Assert.Equal(HealthStatus.Healthy, report.Entries["synclane"].Status);
    }

    [PostgresFact]
    public async Task A_host_whose_owner_source_fails_is_degraded_and_names_the_queue()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.ListenConnectionString = ListenConnectionString;
            options.Schema = Schema;
            options.RunWorkers = false;
            options.AddQueue(Queue, queue => queue.Owners<FailingOwners>().Snapshot<ProductHandler>("products"));
        });
        builder.Services.AddHealthChecks().AddCheck<SyncLaneHealthCheck>("synclane");
        using var host = builder.Build();
        await host.StartAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        HealthReportEntry entry;
        // Sequential: polling until the first planning round has run.
        do
        {
            await Task.Delay(50, timeout.Token);
            entry = (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(timeout.Token)).Entries["synclane"];
        }
        while (entry.Status == HealthStatus.Healthy);

        await host.StopAsync();
        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Contains($"Planning queue {Queue} has failed", entry.Description);
    }

    private sealed class FailingOwners : IOwnerSource
    {
        public Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The account service is down.");
    }

    [PostgresFact]
    public async Task A_database_that_cannot_be_reached_is_unhealthy_and_named_by_its_error_type_only()
    {
        const string unreachable = "Host=127.0.0.1;Port=1;Username=nobody;Database=nowhere;Timeout=2";
        await using var source = NpgsqlDataSource.Create(unreachable);
        _signals.Heartbeat(Noon);

        var result = await CheckAsync(new JobStore(source, new JobSql(Schema)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("cannot reach its database", result.Description);
        Assert.DoesNotContain("127.0.0.1", result.Description);
        Assert.DoesNotContain("nowhere", result.Description);
        Assert.Null(result.Exception);
    }

    [PostgresFact]
    public async Task A_schema_without_its_migrations_is_unhealthy_and_says_how_to_apply_them()
    {
        _signals.Heartbeat(Noon);

        var result = await CheckAsync(new JobStore(Store.DataSource, new JobSql($"{Schema}_missing")));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains($"lacks {SyncLaneSchema.LatestVersion} migrations", result.Description);
        Assert.Contains("SyncLaneSchema.Script()", result.Description);
    }

    [PostgresFact]
    public async Task An_instance_that_has_not_registered_is_degraded_and_one_whose_heartbeat_is_too_old_is_unhealthy()
    {
        var starting = await CheckAsync();
        _signals.Heartbeat(Noon);
        _clock.Set(Noon + InstanceRules.GoneAfter);
        var silent = await CheckAsync();

        Assert.Equal(HealthStatus.Degraded, starting.Status);
        Assert.Equal(HealthStatus.Unhealthy, silent.Status);
        Assert.Contains("other instances count it as gone", silent.Description);
    }

    [PostgresFact]
    public async Task A_queue_whose_planning_fails_is_degraded_until_a_round_succeeds()
    {
        _signals.Heartbeat(Noon);
        _signals.PlanFailed(Queue, Noon);
        var failing = await CheckAsync();
        _signals.Planned(Queue);
        var recovered = await CheckAsync();

        Assert.Equal(HealthStatus.Degraded, failing.Status);
        Assert.Contains($"Planning queue {Queue} has failed", failing.Description);
        Assert.Equal(HealthStatus.Healthy, recovered.Status);
    }

    [PostgresFact]
    public async Task An_instance_that_cannot_hear_job_changes_is_degraded_and_names_the_setting()
    {
        var other = new NpgsqlConnectionStringBuilder(ListenConnectionString!);
        other.Database = other.Database == "postgres" ? "template1" : "postgres";
        await using var feed = new JobChangeFeed(other.ConnectionString, Store.DataSource, Schema, _clock, NullLogger<JobChangeFeed>.Instance)
        {
            ProbePatience = TimeSpan.FromMilliseconds(200)
        };
        _ = feed.ListeningAsync(CancellationToken.None);
        _signals.Heartbeat(Noon);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Sequential: polling until the feed has given up.
        while (feed.State != FeedState.Stopped)
            await Task.Delay(20, timeout.Token);
        var result = await CheckAsync(feed: feed);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("ListenConnectionString", result.Description);
    }

    private async Task<HealthCheckResult> CheckAsync(JobStore? store = null, JobChangeFeed? feed = null)
    {
        await using var idle = new JobChangeFeed(ConnectionString!, Store.DataSource, Schema, _clock, NullLogger<JobChangeFeed>.Instance);
        var check = new SyncLaneHealthCheck(store ?? Store, _signals, feed ?? idle, _clock);
        return await check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("synclane", check, HealthStatus.Unhealthy, null)
        });
    }
}
