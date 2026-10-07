using Microsoft.Extensions.Hosting;

namespace SyncLane.Storage;

/// <summary>Creates the table and its indexes before the planner and the workers start.</summary>
internal sealed class SchemaInitializer(JobStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchemaAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
