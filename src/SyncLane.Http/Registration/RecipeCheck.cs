using Microsoft.Extensions.Hosting;

namespace SyncLane.Http;

/// <summary>
/// Compares the recipes with the declared job types before any hosted service starts, so a missing or misspelt recipe
/// stops the application at start-up instead of abandoning jobs later.
/// </summary>
internal sealed class RecipeCheck(IEnumerable<SyncLaneOptions> engine, RecipeRegistry recipes) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (engine.FirstOrDefault() is not { } options)
            return Task.CompletedTask;

        var declared = options.Describe()
            .SelectMany(queue => queue.JobTypes
                .Where(type => type.Handler == typeof(HttpRecipeHandler))
                .Select(type => (Queue: queue.Name, Type: type.Name)))
            .ToList();

        var errors = declared
            .Where(type => !recipes.TryGet(type.Type, out _))
            .Select(type => HttpErrors.RecipeMissing(type.Queue, type.Type))
            .Concat(recipes.JobTypes
                .Where(type => declared.TrueForAll(found => found.Type != type))
                .Select(HttpErrors.RecipeUnused))
            .ToList();

        return errors.Count == 0 ? Task.CompletedTask : throw new SyncLaneException(errors);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
