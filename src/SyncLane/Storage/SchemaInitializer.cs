using Microsoft.Extensions.Hosting;

namespace SyncLane.Storage;

/// <summary>Applies missing migrations before the planner and the workers start.</summary>
internal sealed class SchemaInitializer(JobStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.MigrateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
