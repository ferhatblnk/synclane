using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace SyncLane.Http;

/// <summary>The Postgres destinations of the registered recipes: one writer per recipe, one connection pool per database.</summary>
internal sealed class PostgresSinks(SyncLaneOptions engine, TimeProvider time, ILoggerFactory loggers) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<PostgresSinkOptions, PostgresWriter> _writers = new();

    public IRecordSink For(PostgresSinkOptions options) => _writers.GetOrAdd(options, Create);

    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(_sources.Values.Select(source => source.DisposeAsync().AsTask()));

    private PostgresWriter Create(PostgresSinkOptions options)
    {
        var source = _sources.GetOrAdd(options.ConnectionString ?? engine.ConnectionString, NpgsqlDataSource.Create);
        return new PostgresWriter(options, source, options.Schema ?? engine.Schema, engine.CreateSchema, time, loggers.CreateLogger<PostgresWriter>());
    }
}

/// <summary>
/// Writes a recipe's pages to one table, one statement per page. A repeated page changes nothing: rows are keyed by
/// owner and key, a row is rewritten only when its record changed, and an older version never replaces a newer one.
/// </summary>
internal sealed partial class PostgresWriter : IRecordSink
{
    private readonly PostgresSinkOptions _options;
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly bool _createTable;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _upsert;
    private readonly SemaphoreSlim _ready = new(1, 1);
    private volatile bool _isReady;

    public PostgresWriter(PostgresSinkOptions options, NpgsqlDataSource source, string schema, bool createTable, TimeProvider time, ILogger logger)
    {
        _options = options;
        _source = source;
        _schema = schema;
        _createTable = createTable;
        _time = time;
        _logger = logger;

        // Both names were checked as plain identifiers when the recipe was registered.
        var table = $"{schema}.{options.Table}";
        _upsert =
            $"""
            INSERT INTO {table} AS t (owner, key, record, version, first_seen_at, changed_at)
            SELECT @owner, u.key, u.record, u.version, @now, @now
            FROM unnest(@keys, @records, @versions) AS u(key, record, version)
            ON CONFLICT (owner, key) DO UPDATE
            SET record = EXCLUDED.record, version = EXCLUDED.version, changed_at = EXCLUDED.changed_at
            WHERE t.record IS DISTINCT FROM EXCLUDED.record
              AND (t.version IS NULL OR EXCLUDED.version >= t.version)
            """;
    }

    public async Task WriteAsync(RecordPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var identified = _options.Identity.Identify(page);
        if (!identified.Succeeded)
            throw new JobAbandonedException(string.Join("; ", identified.Errors));

        var records = identified.Value!;
        if (records.Count == 0)
            return;

        await EnsureTableAsync(page.Recipe, cancellationToken);

        var keys = new string[records.Count];
        var bodies = new string[records.Count];
        var versions = new decimal?[records.Count];
        for (var index = 0; index < records.Count; index++)
        {
            keys[index] = records[index].Key;
            bodies[index] = records[index].Record.GetRawText();
            versions[index] = records[index].Version;
        }

        await using var command = _source.CreateCommand(_upsert);
        command.Parameters.AddWithValue("owner", page.Job.Owner);
        command.Parameters.AddWithValue("now", _time.GetUtcNow().ToUniversalTime());
        command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = keys });
        command.Parameters.Add(new NpgsqlParameter("records", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = bodies });
        command.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = versions });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Creates the table on the first page, or checks it exists when <c>CreateSchema</c> is off. Once per process.</summary>
    private async Task EnsureTableAsync(string recipe, CancellationToken cancellationToken)
    {
        if (_isReady)
            return;

        await _ready.WaitAsync(cancellationToken);
        try
        {
            if (_isReady)
                return;

            if (_createTable)
            {
                await using var create = _source.CreateCommand(PostgresSinkSchema.Script(_schema, _options.Table));
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var exists = _source.CreateCommand("SELECT to_regclass(@name) IS NOT NULL");
                exists.Parameters.AddWithValue("name", $"{_schema}.{_options.Table}");
                if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new JobAbandonedException(HttpErrors.SinkTableMissing(_schema, _options.Table).ToString());
            }

            LogReady(recipe, _schema, _options.Table, _options.Identity.Description, _options.Identity.Version ?? "none");
            _isReady = true;
        }
        finally
        {
            _ready.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Records of recipe {Recipe} go to {Schema}.{Table}, keyed by {Key}, versioned by {Version}.")]
    private partial void LogReady(string recipe, string schema, string table, string key, string version);
}
