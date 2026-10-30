using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
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

        if (options.RunPlanner)
            services.AddHostedService<PlannerService>();

        if (options.RunWorkers)
            services.AddHostedService<JobWorker>();

        return services;
    }
}
