using Demo.Shop;
using Npgsql;
using NpgsqlTypes;

namespace Demo;

/// <summary>
/// The demo's own tables. Every write is an upsert, because a page can be delivered twice:
/// after a crash, and wherever two windows overlap.
/// </summary>
internal sealed class DemoData(NpgsqlDataSource database)
{
    private const string CreateTables =
        """
        CREATE SCHEMA IF NOT EXISTS demo;

        CREATE TABLE IF NOT EXISTS demo.products (
            account text NOT NULL,
            sku     text NOT NULL,
            name    text NOT NULL,
            PRIMARY KEY (account, sku)
        );

        CREATE TABLE IF NOT EXISTS demo.orders (
            account   text           NOT NULL,
            order_id  text           NOT NULL,
            placed_at timestamptz    NOT NULL,
            total     numeric(12, 2) NOT NULL,
            PRIMARY KEY (account, order_id)
        );
        """;

    private const string UpsertProducts =
        """
        INSERT INTO demo.products (account, sku, name)
        SELECT @account, t.sku, t.name
        FROM unnest(@skus, @names) AS t(sku, name)
        ON CONFLICT (account, sku) DO UPDATE SET name = EXCLUDED.name
        """;

    private const string UpsertOrders =
        """
        INSERT INTO demo.orders (account, order_id, placed_at, total)
        SELECT @account, t.order_id, t.placed_at, t.total
        FROM unnest(@order_ids, @placed_ats, @totals) AS t(order_id, placed_at, total)
        ON CONFLICT (account, order_id) DO UPDATE SET placed_at = EXCLUDED.placed_at, total = EXCLUDED.total
        """;

    private const string CountOrders = "SELECT account, count(*)::int FROM demo.orders GROUP BY account";

    private const string CountProducts = "SELECT account, count(*)::int FROM demo.products GROUP BY account";

    public async Task CreateTablesAsync(CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(CreateTables);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveProductsAsync(string account, IReadOnlyList<ShopProduct> products, CancellationToken cancellationToken)
    {
        if (products.Count == 0)
            return;

        await using var command = database.CreateCommand(UpsertProducts);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("skus", products.Select(product => product.Sku).ToArray());
        command.Parameters.AddWithValue("names", products.Select(product => product.Name).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveOrdersAsync(string account, IReadOnlyList<ShopOrder> orders, CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
            return;

        await using var command = database.CreateCommand(UpsertOrders);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("order_ids", orders.Select(order => order.Id).ToArray());
        command.Parameters.Add(new NpgsqlParameter("placed_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            Value = orders.Select(order => order.PlacedAt.ToUniversalTime()).ToArray()
        });
        command.Parameters.AddWithValue("totals", orders.Select(order => order.Total).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<Dictionary<string, int>> CountOrdersAsync(CancellationToken cancellationToken) =>
        CountAsync(CountOrders, cancellationToken);

    public Task<Dictionary<string, int>> CountProductsAsync(CancellationToken cancellationToken) =>
        CountAsync(CountProducts, cancellationToken);

    private async Task<Dictionary<string, int>> CountAsync(string statement, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>();

        await using var command = database.CreateCommand(statement);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }
}
