using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>
/// Where and how a recipe's records are written to PostgreSQL, set in
/// <see cref="HttpRecipeBinding.WriteToPostgres"/>. Every setting has a default: the database and schema SyncLane
/// already uses, and records keyed by <c>$.id</c> when every record has a unique one.
/// </summary>
public sealed partial class PostgresSinkOptions
{
    private readonly RecordKeySettings _keys = new();

    internal PostgresSinkOptions(string table) => Table = table;

    /// <summary>The table, a lower-case identifier such as <c>shop_orders</c>. Created at first use when <c>CreateSchema</c> is on.</summary>
    public string Table { get; }

    /// <summary>The schema of the table. Defaults to the schema SyncLane keeps its jobs in.</summary>
    public string? Schema { get; set; }

    /// <summary>The database. Defaults to the one SyncLane keeps its jobs in.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Keys records by the values at <paramref name="paths"/>, such as <c>$.id</c>, or several for a composite key.</summary>
    public PostgresSinkOptions KeyedBy(params string[] paths)
    {
        _keys.KeyedBy(paths);
        return this;
    }

    /// <summary>Keys records by a hash of their content: for records that never change, such as events or ledger lines.</summary>
    public PostgresSinkOptions KeyedByContent()
    {
        _keys.KeyedByContent();
        return this;
    }

    /// <summary>
    /// Reads a version at <paramref name="path"/>, a number or an ISO 8601 time, so an older copy of a record never
    /// replaces a newer one. Use the field the API updates when a record changes, such as <c>$.updated_at</c>.
    /// </summary>
    public PostgresSinkOptions VersionedBy(string path)
    {
        _keys.VersionedBy(path);
        return this;
    }

    internal RecordIdentity Identity => _keys.Identity;

    /// <summary>Checks the settings for <paramref name="type"/>, and builds the identity when they can work.</summary>
    internal List<SyncError> Validate(string type)
    {
        var errors = new List<SyncError>();
        if (!Identifier().IsMatch(Table))
            errors.Add(HttpErrors.SinkSettingInvalid(type, "table", $"'{Table}' must be a lower-case identifier: letters, digits and underscores, starting with a letter or underscore, at most 63"));

        if (Schema is not null && !Identifier().IsMatch(Schema))
            errors.Add(HttpErrors.SinkSettingInvalid(type, "schema", $"'{Schema}' must be a lower-case identifier: letters, digits and underscores, starting with a letter or underscore, at most 63"));

        if (ConnectionString is not null && string.IsNullOrWhiteSpace(ConnectionString))
            errors.Add(HttpErrors.SinkSettingInvalid(type, "connection string", "must not be empty; leave it out to use SyncLane's database"));

        _keys.Validate(type, errors);
        return errors;
    }

    internal static bool IsIdentifier(string name) => Identifier().IsMatch(name);

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}

/// <summary>
/// The SQL of a Postgres destination's table, for teams that apply migrations themselves. Put <see cref="Script"/> in
/// your own migration and set <c>CreateSchema</c> to <c>false</c>.
/// </summary>
public static class PostgresSinkSchema
{
    /// <summary>
    /// Creates the table if it is missing. One row per owner and key: the latest record, its version, when the key was
    /// first seen and when the record last changed. Safe to run more than once.
    /// </summary>
    /// <param name="schema">The schema, as in <see cref="PostgresSinkOptions.Schema"/>.</param>
    /// <param name="table">The table, as in <see cref="PostgresSinkOptions.Table"/>.</param>
    public static string Script(string schema, string table)
    {
        if (!PostgresSinkOptions.IsIdentifier(schema))
            throw new ArgumentException(HttpErrors.SinkSettingInvalid(null, "schema", $"'{schema}' must be a lower-case identifier").ToString(), nameof(schema));

        if (!PostgresSinkOptions.IsIdentifier(table))
            throw new ArgumentException(HttpErrors.SinkSettingInvalid(null, "table", $"'{table}' must be a lower-case identifier").ToString(), nameof(table));

        return $"""
            CREATE SCHEMA IF NOT EXISTS {schema};

            CREATE TABLE IF NOT EXISTS {schema}.{table} (
                owner         text        NOT NULL,
                key           text        NOT NULL,
                record        jsonb       NOT NULL,
                version       numeric,
                first_seen_at timestamptz NOT NULL,
                changed_at    timestamptz NOT NULL,
                PRIMARY KEY (owner, key)
            );
            """;
    }
}
