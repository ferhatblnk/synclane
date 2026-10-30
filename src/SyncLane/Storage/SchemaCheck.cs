using Microsoft.Extensions.Hosting;

namespace SyncLane.Storage;

/// <summary>
/// Used when <see cref="SyncLaneOptions.CreateSchema"/> is off. Stops the host at start-up when a migration is missing,
/// with a message that says how to apply it, instead of failing later on the first query.
/// </summary>
internal sealed class SchemaCheck(JobStore store) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = await store.MissingMigrationsAsync(cancellationToken);
        if (missing.Count == 0)
            return;

        throw new SyncLaneException(SyncLaneErrors.MigrationMissing(store.Schema, missing));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
