using System.Text.Json.Serialization;
using Demo;
using Demo.Shop;
using Npgsql;
using SyncLane;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Postgres to the database the demo may use.");

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<DemoData>();
builder.Services.AddSingleton<PretendShop>();
builder.Services.AddSingleton<ConnectedAccounts>();

builder.Services.AddSyncLane(lane =>
{
    lane.ConnectionString = connectionString;
    lane.PlanInterval = TimeSpan.FromSeconds(10);
    lane.PollInterval = TimeSpan.FromSeconds(1);

    lane.AddQueue(Dashboard.Queue, queue =>
    {
        queue.Concurrency = 3;
        queue.LeaseDuration = TimeSpan.FromSeconds(30);
        queue.RetryBaseDelay = TimeSpan.FromSeconds(2);
        queue.RetryMaxDelay = TimeSpan.FromSeconds(15);
        queue.ThrottleBaseDelay = TimeSpan.FromSeconds(3);
        queue.ThrottleMaxDelay = TimeSpan.FromSeconds(10);

        queue.Owners<ConnectedAccounts>();

        queue.Snapshot<ProductHandler>("products", every: TimeSpan.FromMinutes(2));

        queue.Windowed<OrderHandler>(
            "orders",
            horizon: TimeSpan.FromDays(365),
            slice: TimeSpan.FromDays(30),
            every: TimeSpan.FromSeconds(30),
            overlap: TimeSpan.FromSeconds(5));
    });
});

var app = builder.Build();

await app.Services.GetRequiredService<DemoData>().CreateTablesAsync(CancellationToken.None);
app.MapDashboard();
app.Run();
