using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

/// <summary>The Postgres destination: its own table, written one statement per page, so that a repeated page changes nothing.</summary>
public class PostgresSinkTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);
    private readonly List<PostgresSinks> _sinks = [];

    [PostgresFact]
    public async Task The_table_is_created_on_the_first_page_with_one_row_per_owner_and_key()
    {
        var sink = Sink(options => { });

        await sink.WriteAsync(Page("shop-7", """[{ "id": 1, "total": 10 }, { "id": 2, "total": 20 }]"""), CancellationToken.None);

        var rows = await RowsAsync();
        Assert.Equal([("shop-7", "1", 10), ("shop-7", "2", 20)], rows.Select(row => (row.Owner, row.Key, row.Total)));
        Assert.All(rows, row => Assert.Equal((Noon, Noon), (row.FirstSeenAt, row.ChangedAt)));
    }

    [PostgresFact]
    public async Task A_page_written_twice_changes_nothing()
    {
        var sink = Sink(options => { });
        var page = """[{ "id": 1, "total": 10 }, { "id": 2, "total": 20 }]""";

        await sink.WriteAsync(Page("shop-7", page), CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(1));
        await sink.WriteAsync(Page("shop-7", page), CancellationToken.None);

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(Noon, row.ChangedAt));
    }

    [PostgresFact]
    public async Task A_changed_record_replaces_its_row_and_keeps_when_the_key_was_first_seen()
    {
        var sink = Sink(options => { });

        await sink.WriteAsync(Page("shop-7", """[{ "id": 1, "total": 10 }]"""), CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(1));
        await sink.WriteAsync(Page("shop-7", """[{ "id": 1, "total": 15 }]"""), CancellationToken.None);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal((15, Noon, Noon.AddHours(1)), (row.Total, row.FirstSeenAt, row.ChangedAt));
    }

    [PostgresFact]
    public async Task An_older_version_never_replaces_a_newer_one_and_a_record_without_a_version_never_replaces_one_with()
    {
        var sink = Sink(options => options.VersionedBy("$.updated_at"));

        await sink.WriteAsync(Page("shop-7", """
            [{ "id": 1, "total": 30, "updated_at": "2026-10-31T12:00:00Z" },
             { "id": 2, "total": 30, "updated_at": "2026-10-31T12:00:00Z" }]
            """), CancellationToken.None);

        // Fetched earlier by another job, written later: an older copy of 1, and a copy of 2 without a version.
        await sink.WriteAsync(Page("shop-7", """
            [{ "id": 1, "total": 10, "updated_at": "2026-10-31T14:00:00+03:00" },
             { "id": 2, "total": 10 }]
            """), CancellationToken.None);
        await sink.WriteAsync(Page("shop-7", """[{ "id": 1, "total": 40, "updated_at": "2026-10-31T12:30:00Z" }]"""), CancellationToken.None);

        Assert.Equal([("1", 40), ("2", 30)], (await RowsAsync()).Select(row => (row.Key, row.Total)));
    }

    [PostgresFact]
    public async Task Owners_with_the_same_key_have_rows_of_their_own()
    {
        var sink = Sink(options => options.KeyedBy("$.number"));

        await sink.WriteAsync(Page("shop-7", """[{ "number": "A-1", "total": 7 }]"""), CancellationToken.None);
        await sink.WriteAsync(Page("shop-8", """[{ "number": "A-1", "total": 8 }]"""), CancellationToken.None);

        Assert.Equal([("shop-7", 7), ("shop-8", 8)], (await RowsAsync()).Select(row => (row.Owner, row.Total)));
    }

    [PostgresFact]
    public async Task A_page_whose_records_cannot_be_keyed_stops_the_job_and_writes_nothing()
    {
        var sink = Sink(options => { });

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() =>
            sink.WriteAsync(Page("shop-7", """[{ "id": 1, "number": "A" }, { "number": "B" }]"""), CancellationToken.None));

        Assert.StartsWith(HttpErrorCodes.RecordKeyMissing, thrown.Message);
        Assert.Contains("$.number", thrown.Message);
        Assert.Empty(await RowsAsync(createdIfMissing: false));
    }

    [PostgresFact]
    public async Task With_create_schema_off_a_missing_table_stops_the_job_and_the_script_creates_it()
    {
        var sink = Sink(options => { }, createSchema: false);

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() =>
            sink.WriteAsync(Page("shop-7", """[{ "id": 1, "total": 10 }]"""), CancellationToken.None));
        await ExecuteAsync(PostgresSinkSchema.Script(Schema, "orders"));
        await Sink(options => { }, createSchema: false).WriteAsync(Page("shop-7", """[{ "id": 1, "total": 10 }]"""), CancellationToken.None);

        Assert.StartsWith(HttpErrorCodes.SinkTableMissing, thrown.Message);
        Assert.Single(await RowsAsync());
    }

    [PostgresFact]
    public async Task A_recipe_writes_to_postgres_with_one_line()
    {
        var api = new FakeApi((_, _) => Task.FromResult(FakeApi.Response(HttpStatusCode.OK, """{ "items": [{ "id": 1, "total": 10 }, { "id": 2, "total": 20 }] }""")));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.AddQueue("shops", queue => queue.Handle<HttpRecipeHandler>("orders"));
        });
        services.AddSyncLaneHttp(http => http
            .Recipe("orders", """{ "name": "test/orders", "request": { "url": "https://api.example.com/orders" }, "records": "$.items" }""")
            .WriteToPostgres("orders"));
        services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = new Job { Id = 1, Queue = "shops", Owner = "shop-7", Lane = "shop-7", Type = "orders", Mode = JobMode.Initial, Status = JobStatus.Running };
            await scope.ServiceProvider.GetRequiredService<HttpRecipeHandler>().RunPageAsync(job, CancellationToken.None);
        }

        Assert.Equal([("1", 10), ("2", 20)], (await RowsAsync()).Select(row => (row.Key, row.Total)));
    }

    public override async Task DisposeAsync()
    {
        await Task.WhenAll(_sinks.Select(sinks => sinks.DisposeAsync().AsTask()));
        await base.DisposeAsync();
    }

    /// <summary>A fresh destination, as a newly started process would have.</summary>
    private IRecordSink Sink(Action<PostgresSinkOptions> configure, bool createSchema = true)
    {
        var options = new PostgresSinkOptions("orders");
        configure(options);
        Assert.Empty(options.Validate("orders"));

        var sinks = new PostgresSinks(new SyncLaneOptions { ConnectionString = ConnectionString!, Schema = Schema, CreateSchema = createSchema }, _clock, NullLoggerFactory.Instance);
        _sinks.Add(sinks);
        return sinks.For(options);
    }

    private static RecordPage Page(string owner, string records)
    {
        var job = new Job { Id = 1, Queue = "shops", Owner = owner, Lane = owner, Type = "orders", Mode = JobMode.Initial, Status = JobStatus.Running };
        return new RecordPage(job, "test/orders", JsonDocument.Parse(records).RootElement);
    }

    private async Task<List<Row>> RowsAsync(bool createdIfMissing = true)
    {
        await using var source = Npgsql.NpgsqlDataSource.Create(ConnectionString!);
        await using (var exists = source.CreateCommand($"SELECT to_regclass('{Schema}.orders') IS NOT NULL"))
        {
            if (await exists.ExecuteScalarAsync() is not true)
                return createdIfMissing ? throw new InvalidOperationException("The table was not created.") : [];
        }

        await using var command = source.CreateCommand(
            $"SELECT owner, key, (record->>'total')::int, first_seen_at, changed_at FROM {Schema}.orders ORDER BY owner, key");
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Row>();

        // Sequential: reading rows from one data reader.
        while (await reader.ReadAsync())
            rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4)));

        return rows;
    }

    private sealed record Row(string Owner, string Key, int Total, DateTimeOffset FirstSeenAt, DateTimeOffset ChangedAt);
}

public class PostgresSinkSettingsTests
{
    [Fact]
    public void Settings_that_cannot_work_are_refused_when_the_recipe_is_registered()
    {
        var thrown = Assert.Throws<SyncLaneException>(() => new ServiceCollection().AddSyncLaneHttp(http => http
            .Recipe("orders", """{ "name": "test/orders", "request": { "url": "https://api.example.com/orders" } }""")
            .WriteToPostgres("Shop Orders", options => options.KeyedBy("id").VersionedBy("$"))));

        Assert.Equal([HttpErrorCodes.SinkSettingInvalid, HttpErrorCodes.SinkSettingInvalid, HttpErrorCodes.SinkSettingInvalid], thrown.Errors.Select(error => error.Code));
    }
}
