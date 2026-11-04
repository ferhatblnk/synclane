namespace SyncLane.Storage;

/// <summary>The statements on the instances table, built once for the configured schema.</summary>
internal sealed class InstanceSql
{
    public const string TableName = "instances";

    public InstanceSql(string schema)
    {
        var table = $"{schema}.{TableName}";

        // Instances that start together register one after the other, so two applications cannot both see a queue free.
        Lock = "SELECT pg_advisory_xact_lock(hashtext(@lock))";
        LockKey = $"synclane.instances:{schema}";

        Live =
            $"""
            SELECT id, application, host, version, queues, budgets::text, started_at, heartbeat_at
            FROM {table}
            WHERE id <> @id AND heartbeat_at > @alive_since
            """;

        // Every value is written again on a heartbeat, so a row deleted while the instance was silent comes back.
        Upsert =
            $"""
            INSERT INTO {table} (id, application, host, version, queues, budgets, started_at, heartbeat_at)
            VALUES (@id, @application, @host, @version, @queues, @budgets::jsonb, @started_at, @now)
            ON CONFLICT (id) DO UPDATE
            SET application = excluded.application,
                host = excluded.host,
                version = excluded.version,
                queues = excluded.queues,
                budgets = excluded.budgets,
                started_at = excluded.started_at,
                heartbeat_at = excluded.heartbeat_at
            """;

        Forget = $"DELETE FROM {table} WHERE heartbeat_at < @forget_before";

        Remove = $"DELETE FROM {table} WHERE id = @id";

        All = $"SELECT id, application, host, version, queues, budgets::text, started_at, heartbeat_at FROM {table} ORDER BY application, id";
    }

    public string All { get; }

    public string Lock { get; }

    public string LockKey { get; }

    public string Live { get; }

    public string Upsert { get; }

    public string Forget { get; }

    public string Remove { get; }
}
