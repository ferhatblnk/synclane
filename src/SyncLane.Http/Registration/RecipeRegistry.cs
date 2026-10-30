using System.Diagnostics.CodeAnalysis;

namespace SyncLane.Http;

/// <summary>The recipes registered with <c>AddSyncLaneHttp</c>, by job type. Read-only once built.</summary>
internal sealed class RecipeRegistry(IEnumerable<HttpRecipeBinding> bindings)
{
    private readonly Dictionary<string, HttpRecipeBinding> _byType =
        bindings.ToDictionary(binding => binding.JobType, StringComparer.Ordinal);

    public IEnumerable<string> JobTypes => _byType.Keys;

    public bool TryGet(string jobType, [NotNullWhen(true)] out HttpRecipeBinding? binding) =>
        _byType.TryGetValue(jobType, out binding);
}
