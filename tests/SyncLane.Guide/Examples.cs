namespace GuideExamples;

// Step 3. The owner source
public sealed class ConnectedShops(AppDb db) : IOwnerSource
{
    public async Task<IReadOnlyList<JobOwner>> GetOwnersAsync(string queue, CancellationToken cancellationToken) =>
        await db.Shops
            .Where(shop => shop.IsConnected)
            .Select(shop => new JobOwner(shop.Id.ToString(), null, shop.Name)
            {
                Priority = shop.IsNew ? JobPriority.High : JobPriority.Normal
            })
            .ToListAsync(cancellationToken);
}

// Step 4a. A handler
public sealed class OrderHandler(IShopClient client, OrderRepository orders, RateBudgets budgets) : IJobHandler
{
    public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
    {
        var permit = await budgets.WaitAsync("shop-api", job.Owner, cancellationToken);
        var response = await client.GetOrdersAsync(job.Owner, job.WindowStart, job.WindowEnd, job.Cursor, cancellationToken);

        if (response.IsThrottled)
        {
            var pause = await budgets.ThrottledAsync(permit, response.RetryAfter, cancellationToken);
            throw new JobPostponedException("The shop API asked to slow down.", pause);
        }

        if (response.IsUnauthorized)
            throw new JobAbandonedException("The shop's access was revoked; reconnect it.");

        await orders.UpsertAsync(job.Owner, response.Orders, cancellationToken);
        return new JobPage(response.NextCursor, response.Orders.Count);
    }
}

// Step 4b. A recipe
public sealed class ShopCredentials(AppDb db) : ICredentialStore
{
    public async ValueTask<Credential?> GetAsync(CredentialKey key, CancellationToken cancellationToken)
    {
        var shop = await db.Shops.FindAsync(new object[] { int.Parse(key.Owner) }, cancellationToken);
        return shop is { IsConnected: true } ? new Credential { Token = shop.AccessToken } : null;
    }

    public async ValueTask SaveRefreshTokenAsync(CredentialKey key, string refreshToken, CancellationToken cancellationToken)
    {
        var shop = await db.Shops.FindAsync(new object[] { int.Parse(key.Owner) }, cancellationToken);
        shop!.RefreshToken = refreshToken;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The registrations and calls of the guide, each in the method that gives it the names it uses.</summary>
public static class Steps
{
    public static void Recipe(WebApplicationBuilder builder)
    {
        builder.Services.AddSyncLaneHttp(http =>
        {
            http.Credentials<ShopCredentials>();
            http.Recipe("orders", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sync/Recipes/orders.jsonc")))
                .WriteToPostgres("shop_orders", table => table.VersionedBy("$.updated_at"));
        });
    }

    public static void Queue(WebApplicationBuilder builder)
    {
        builder.Services.AddSyncLane(builder.Configuration.GetSection("SyncLane"), lane =>
        {
            lane.ConnectionString = builder.Configuration.GetConnectionString("Postgres")!;
            lane.AddBudget("shop-api", limit: 2, per: TimeSpan.FromSeconds(1), burst: 10);

            lane.AddQueue("shop.stores", queue => queue
                .Owners<ConnectedShops>()
                .Windowed<OrderHandler>("orders", horizon: TimeSpan.FromDays(30), slice: TimeSpan.FromDays(7),
                    every: TimeSpan.FromMinutes(15), overlap: TimeSpan.FromMinutes(10))
                .Snapshot<ProductHandler>("products", every: TimeSpan.FromHours(6))
                .Handle<InvoiceHandler>("invoice-upload"));
        });
    }

    public static async Task SwitchesAsync(ISyncLaneMonitor monitor, string user)
    {
        await monitor.SetSwitchAsync("shop", role: null, runWorkers: false, runPlanner: false, reason: "web instances stay idle", by: user);
        await monitor.SetSwitchAsync("shop", role: "worker", runWorkers: true, runPlanner: true, reason: "the worker syncs", by: user);
    }

    public static void WorkerOnly(WebApplicationBuilder builder)
    {
        if (builder.Configuration["SyncLane:Role"] == "worker")
            builder.Services.AddHostedService<ExpiredDataCleanup>();
    }

    public static void HealthAndTelemetry(WebApplicationBuilder builder, WebApplication app)
    {
        builder.Services.AddHealthChecks().AddCheck<SyncLaneHealthCheck>("synclane");
        app.MapHealthChecks("/health");

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter(SyncLaneTelemetry.MeterName))
            .WithTracing(tracing => tracing.AddSource(SyncLaneTelemetry.ActivitySourceName));
    }

    public static void Monitor(WebApplication app)
    {
        app.MapSyncLane("/synclane").RequireAuthorization("ops");
    }

    public static async Task UpgradeAsync(ISyncLaneMonitor monitor, string user)
    {
        await monitor.SetSwitchAsync("shop", role: null, runWorkers: false, runPlanner: false, reason: "web instances, as their settings said", by: user);
        await monitor.SetSwitchAsync("shop", role: "worker", runWorkers: true, runPlanner: true, reason: "the worker, as its settings said", by: user);
        await monitor.SetListeningAsync("shop", role: null, listen: false, reason: "behind the pooler, as its settings said", by: user);
    }

    public static async Task RefreshAsync(ISyncLaneMonitor monitor, ISyncLane syncLane, Shop shop, User user)
    {
        await monitor.SyncNowAsync("shop.stores", owner: shop.Id.ToString(), type: "orders", by: user.Id);
        await syncLane.PlanAsync("shop.stores", new JobOwner(shop.Id.ToString(), Name: shop.Name));
        var progress = await syncLane.GetProgressAsync("shop.stores", shop.Id.ToString());
    }

    public static async Task SendAsync(ISyncLane syncLane, Shop shop, Invoice invoice, User user)
    {
        await syncLane.EnqueueOrReplaceAsync(new NewJob
        {
            Queue = "shop.stores",
            Owner = new JobOwner(shop.Id.ToString(), Name: shop.Name),
            Type = "invoice-upload",
            DedupKey = $"invoice:{invoice.Id}",
            Arguments = JsonSerializer.Serialize(invoice),
            RequestedBy = user.Id
        });
        await syncLane.CompleteAsync("shop.stores", shop.Id.ToString(), "invoice-upload", [$"invoice:{invoice.Id}"]);
    }
}

/// <summary>Section 5: the end-to-end test.</summary>
public sealed class EndToEnd : EndToEndHelpers
{
    [Fact]
    public async Task A_connected_shops_orders_reach_the_orders_table()
    {
        var schema = $"t_{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = TestDatabase.ConnectionString,
            ["SyncLane:Schema"] = schema,
            ["SyncLane:PlanInterval"] = "00:00:01",
            ["SyncLane:PollInterval"] = "00:00:01",
        });
        builder.Services.AddShopSync(builder.Configuration);
        builder.Services.AddSingleton<IShopClient>(new FakeShopClient(ordersPerPage: 100, pages: 3));
        using var host = builder.Build();
        await host.StartAsync();

        var syncLane = host.Services.GetRequiredService<ISyncLane>();
        await WaitUntilAsync(async () =>
        {
            var all = await syncLane.GetJobsAsync(new JobQuery { Queue = "shop.stores", IncludeTotal = true });
            var done = await syncLane.GetJobsAsync(new JobQuery { Queue = "shop.stores", Status = JobStatus.Completed, IncludeTotal = true });
            return all.Total > 0 && done.Total == all.Total;
        }, timeout: TimeSpan.FromMinutes(1));

        Assert.Equal(300, await CountOrdersAsync(shopId: TestShops.First));
        await host.StopAsync();
        await DropSchemaAsync(schema);
    }
}
