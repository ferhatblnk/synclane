namespace SyncLane.Http;

/// <summary>A recipe registered for a job type, waiting to be told where its records go.</summary>
public sealed class HttpRecipeBinding
{
    internal HttpRecipeBinding(string jobType, CompiledRecipe recipe)
    {
        JobType = jobType;
        Recipe = recipe;
    }

    internal string JobType { get; }

    internal CompiledRecipe Recipe { get; }

    internal Type? SinkType { get; private set; }

    internal Func<RecordPage, CancellationToken, Task>? SinkDelegate { get; private set; }

    /// <summary>Writes the records with <typeparamref name="TSink"/>, resolved from a scope of its own for every page.</summary>
    public HttpRecipeBinding WriteTo<TSink>() where TSink : class, IRecordSink
    {
        SinkType = typeof(TSink);
        SinkDelegate = null;
        return this;
    }

    /// <summary>Writes the records with <paramref name="write"/>.</summary>
    public HttpRecipeBinding WriteTo(Func<RecordPage, CancellationToken, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        SinkDelegate = write;
        SinkType = null;
        return this;
    }

    internal bool HasSink => SinkType is not null || SinkDelegate is not null;
}
