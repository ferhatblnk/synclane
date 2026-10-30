using System.Text.Json;

namespace SyncLane.Http;

/// <summary>One page of records, as a recipe found them in the response.</summary>
/// <remarks>
/// <see cref="Records"/> points into a buffer that is released when <see cref="IRecordSink.WriteAsync"/> returns.
/// Read or copy what you need inside the call; call <see cref="JsonElement.Clone"/> on anything you keep.
/// </remarks>
public sealed class RecordPage
{
    internal RecordPage(Job job, string recipe, JsonElement records)
    {
        Job = job;
        Recipe = recipe;
        Records = records;
    }

    /// <summary>The job the page belongs to: owner, type and window.</summary>
    public Job Job { get; }

    /// <summary>The name of the recipe that fetched the page.</summary>
    public string Recipe { get; }

    /// <summary>The records: a JSON array.</summary>
    public JsonElement Records { get; }

    /// <summary>How many records the page holds.</summary>
    public int Count => Records.GetArrayLength();
}
