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

    internal PostgresSinkOptions? Postgres { get; private set; }

    internal HttpSinkOptions? Http { get; private set; }

    /// <summary>Writes the records with <typeparamref name="TSink"/>, resolved from a scope of its own for every page.</summary>
    public HttpRecipeBinding WriteTo<TSink>() where TSink : class, IRecordSink
    {
        SinkType = typeof(TSink);
        SinkDelegate = null;
        Postgres = null;
        Http = null;
        return this;
    }

    /// <summary>Writes the records with <paramref name="write"/>.</summary>
    public HttpRecipeBinding WriteTo(Func<RecordPage, CancellationToken, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        SinkDelegate = write;
        SinkType = null;
        Postgres = null;
        Http = null;
        return this;
    }

    /// <summary>
    /// Writes the records to a PostgreSQL table SyncLane creates and owns: one row per owner and key, holding the latest
    /// record. A repeated page changes nothing, and an older copy never replaces a newer one when a version is named.
    /// Without settings it uses SyncLane's own database and schema, and keys records by <c>$.id</c> when every record
    /// has a unique one; otherwise the job stops with an error that names the fields that could serve.
    /// </summary>
    /// <param name="table">The table, a lower-case identifier such as <c>shop_orders</c>.</param>
    /// <param name="configure">The key, the version, and another schema or database if needed.</param>
    public HttpRecipeBinding WriteToPostgres(string table, Action<PostgresSinkOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var options = new PostgresSinkOptions(table);
        configure?.Invoke(options);
        Postgres = options;
        Http = null;
        SinkType = null;
        SinkDelegate = null;
        return this;
    }

    /// <summary>
    /// Posts the records to <paramref name="url"/>, a page per request, each record with its key and version, as a
    /// Standard Webhooks message. The same records always carry the same <c>webhook-id</c> and <c>Idempotency-Key</c>,
    /// so a receiver can drop a page it has seen. Pages too large are split; failures are retried in the page; a
    /// receiver that asks to slow down postpones the job without using an attempt.
    /// </summary>
    /// <param name="url">The receiver: https, or http on this machine for development.</param>
    /// <param name="configure">A signing secret, headers such as <c>Authorization</c>, the key and the version.</param>
    public HttpRecipeBinding WriteToHttp(string url, Action<HttpSinkOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var options = new HttpSinkOptions(url);
        configure?.Invoke(options);
        Http = options;
        Postgres = null;
        SinkType = null;
        SinkDelegate = null;
        return this;
    }

    internal bool HasSink => SinkType is not null || SinkDelegate is not null || Postgres is not null || Http is not null;
}
