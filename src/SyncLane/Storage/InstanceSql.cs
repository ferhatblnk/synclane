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

        const string columns =
            "id, application, host, version, queues, budgets::text, started_at, heartbeat_at, stopped_at, role, runs_workers, runs_planner, listens";

        // A stopped instance keeps its row so people can see when it stopped, but holds no queue.
        Live =
            $"""
            SELECT {columns}
            FROM {table}
            WHERE id <> @id AND heartbeat_at > @alive_since AND stopped_at IS NULL
            """;

        // Every value is written again on a heartbeat, so a row deleted while the instance was silent comes back, and a
        // process that restarts under the same id is live again.
        Upsert =
            $"""
            INSERT INTO {table} (id, application, host, version, queues, budgets, started_at, heartbeat_at, role, runs_workers, runs_planner, listens)
            VALUES (@id, @application, @host, @version, @queues, @budgets::jsonb, @started_at, @now, @role, @runs_workers, @runs_planner, @listens)
            ON CONFLICT (id) DO UPDATE
            SET application = excluded.application,
                role = excluded.role,
                runs_workers = excluded.runs_workers,
                runs_planner = excluded.runs_planner,
                listens = excluded.listens,
                host = excluded.host,
                version = excluded.version,
                queues = excluded.queues,
                budgets = excluded.budgets,
                started_at = excluded.started_at,
                heartbeat_at = excluded.heartbeat_at,
                stopped_at = NULL
            """;

        Forget = $"DELETE FROM {table} WHERE heartbeat_at < @forget_before";

        Switch = $"SELECT run_workers, run_planner, listen FROM {schema}.switches WHERE application = @application AND role = @role";

        Stop = $"UPDATE {table} SET stopped_at = @now WHERE id = @id";

        All = $"SELECT {columns} FROM {table} ORDER BY application, id";
    }

    public string All { get; }

    public string Lock { get; }

    public string LockKey { get; }

    public string Live { get; }

    public string Upsert { get; }

    public string Forget { get; }

    /// <summary>The switch an operator set for one application and role, if any.</summary>
    public string Switch { get; }

    public string Stop { get; }
}
