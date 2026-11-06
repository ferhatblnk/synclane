using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SyncLane.Instances;
using SyncLane.Monitoring;
using SyncLane.Planning;
using SyncLane.Running;
using SyncLane.Storage;
using SyncLane.Telemetry;

namespace SyncLane;

/// <summary>Registers SyncLane.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the jobs table access, the planner and the workers. Handlers and owner sources are registered as scoped
    /// services; a handler gets a fresh scope for every page.
    /// </summary>
    public static IServiceCollection AddSyncLane(this IServiceCollection services, Action<SyncLaneOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SyncLaneOptions();
        configure(options);
        return Register(services, options);
    }

    /// <summary>
    /// Adds SyncLane as <see cref="AddSyncLane(IServiceCollection, Action{SyncLaneOptions})"/> does, then applies
    /// <paramref name="configuration"/> over what <paramref name="configure"/> declared. Pass the section that holds
    /// SyncLane's settings, usually <c>builder.Configuration.GetSection("SyncLane")</c>. The code declares queues, job
    /// types, handlers and budgets; the configuration changes their numbers per environment: top-level settings such as
    /// <c>RunWorkers</c>, and <c>Queues:&lt;queue&gt;</c>, <c>Queues:&lt;queue&gt;:Types:&lt;type&gt;</c> and
    /// <c>Budgets:&lt;budget&gt;</c>. A name the code does not declare stops the start with SL1012, and a value that
    /// cannot be read with SL1013.
    /// </summary>
    public static IServiceCollection AddSyncLane(this IServiceCollection services, IConfiguration configuration, Action<SyncLaneOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SyncLaneOptions();
        configure(options);
        SettingsFromConfiguration.Apply(options, configuration);
        return Register(services, options);
    }

    private static IServiceCollection Register(IServiceCollection services, SyncLaneOptions options)
    {
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(static provider =>
        {
            var settings = provider.GetRequiredService<SyncLaneOptions>();
            return new JobStore(NpgsqlDataSource.Create(settings.ConnectionString), new JobSql(settings.Schema));
        });
        services.AddSingleton(static provider => new SyncLaneMetrics(provider.GetService<IMeterFactory>()));
        services.AddSingleton(static provider => new BudgetStore(
            provider.GetRequiredService<JobStore>().DataSource,
            new BudgetSql(provider.GetRequiredService<SyncLaneOptions>().Schema)));
        services.AddSingleton(static provider => new RateBudgets(
            provider.GetRequiredService<SyncLaneOptions>(),
            provider.GetRequiredService<BudgetStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<SyncLaneMetrics>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RateBudgets>>()));
        services.AddSingleton(static provider => new InstanceStore(
            provider.GetRequiredService<JobStore>().DataSource,
            new InstanceSql(provider.GetRequiredService<SyncLaneOptions>().Schema)));
        services.AddSingleton(static provider => InstanceIdentity.Create(
            provider.GetRequiredService<SyncLaneOptions>(), provider.GetService<IHostEnvironment>()));
        services.AddSingleton(static provider => new MonitorSql(provider.GetRequiredService<SyncLaneOptions>().Schema));
        services.AddSingleton(static provider => new MonitorStore(
            provider.GetRequiredService<JobStore>().DataSource, provider.GetRequiredService<MonitorSql>()));
        services.AddSingleton(static provider => new JobChangeFeed(
            provider.GetRequiredService<SyncLaneOptions>().ListenConnection,
            provider.GetRequiredService<JobStore>().DataSource,
            provider.GetRequiredService<SyncLaneOptions>().Schema,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JobChangeFeed>>()));
        services.AddSingleton<ISyncLaneMonitor, SyncLaneMonitor>();
        services.AddSingleton<QueuePlanner>();
        services.AddSingleton<RunningJobs>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<ISyncLane, SyncLaneClient>();

        foreach (var queue in options.Queues)
        {
            foreach (var type in queue.Types)
                services.TryAddScoped(type.Handler);

            if (queue.OwnerSource is { } source)
                services.TryAddScoped(source);
        }

        if (options.CreateSchema)
            services.AddHostedService<SchemaInitializer>();
        else
            services.AddHostedService<SchemaCheck>();

        // After the schema, before the planner and the workers: a refused start must happen before any job is taken.
        services.AddHostedService<InstanceRegistry>();

        if (options.RunPlanner)
        {
            services.AddHostedService<PlannerService>();
            services.AddHostedService<RetentionService>();
        }

        if (options.RunWorkers)
            services.AddHostedService<JobWorker>();

        return services;
    }
}
