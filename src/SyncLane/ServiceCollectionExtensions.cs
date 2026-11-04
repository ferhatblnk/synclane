using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SyncLane.Instances;
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
        services.AddSingleton<QueuePlanner>();
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
