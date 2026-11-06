using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

/// <summary>
/// The gate for destinations: pages are written again and again by crashes at random points, against an API whose
/// records change between fetches, and the table still ends with every record once, at its newest version.
/// </summary>
[Collection(WallClockCollection.Name)]
public class DestinationGateTests : PostgresTest
{
    private const int Owners = 6;
    private const int PagesPerJob = 5;
    private const int PerPage = 4;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Crashes_at_random_points_leave_every_record_once_at_its_newest_version(bool keyed)
    {
        var api = new ChangingApi(seed: 7);
        var chaos = new Chaos(seed: 11);
        await using var sinks = new PostgresSinks(new SyncLaneOptions { ConnectionString = ConnectionString!, Schema = Schema }, TimeProvider.System, NullLoggerFactory.Instance);
        var options = new PostgresSinkOptions("orders").VersionedBy("$.updated_at");
        Assert.Empty(options.Validate("items"));
        var writer = sinks.For(options);

        Func<RecordPage, CancellationToken, Task> write = keyed ? writer.WriteAsync : NaiveInsertAsync;
        Func<RecordPage, CancellationToken, Task> crashing = async (page, cancellationToken) =>
        {
            await write(page, cancellationToken);

            // Written, but the cursor is not saved yet: the page runs again.
            if (chaos.Strikes(0.25))
                throw new InvalidOperationException("The worker died after the write.");
        };

        if (!keyed)
            await ExecuteAsync($"CREATE TABLE {Schema}.orders (owner text, key text, record jsonb)");

        // One host runs for a while and stops, then another takes over what is left.
        using (var first = Host(api, crashing))
        {
            await first.StartAsync();
            await EnqueueAsync(first);
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            await first.StopAsync();
        }

        using var second = Host(api, crashing);
        await second.StartAsync();
        await WaitAsync(jobs => jobs.Count == Owners && jobs.All(job => job.Status == JobStatus.Completed));
        await second.StopAsync();

        var rows = await RowsAsync();
        Assert.True(chaos.Struck > 0 && api.Failed > 0, "The run must have had crashes and failures, or it proves nothing.");

        if (!keyed)
        {
            // The same run with a plain INSERT: every page written twice leaves its records twice.
            Assert.True(rows.Count > Owners * PagesPerJob * PerPage, $"{rows.Count} rows; a plain insert should have duplicated some.");
            return;
        }

        Assert.Equal(Owners * PagesPerJob * PerPage, rows.Count);
        Assert.Equal(rows.Count, rows.Select(row => (row.Owner, row.Key)).Distinct().Count());
        Assert.All(rows, row => Assert.Equal(api.NewestServed(row.Owner, row.Key), row.Version));
    }

    [PostgresFact]
    public async Task Crashes_and_lost_answers_leave_a_receiver_with_every_record_once_at_its_newest_version()
    {
        var api = new ChangingApi(seed: 7);
        var chaos = new Chaos(seed: 11);
        var receiver = new ContractReceiver(seed: 13);
        var options = new HttpSinkOptions("https://receiver.example.com/sync") { Secret = ContractReceiver.Secret }.VersionedBy("$.updated_at");
        Assert.Empty(options.Validate("items"));
        options.Pauses = [TimeSpan.Zero, TimeSpan.Zero];

        var services = new ServiceCollection();
        services.AddHttpClient(HttpSource.DestinationClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
        await using var provider = services.BuildServiceProvider();
        var writer = new HttpSinks(provider.GetRequiredService<IHttpClientFactory>(), new HttpSourceOptions(), TimeProvider.System).For(options);

        using var host = Host(api, async (page, cancellationToken) =>
        {
            await writer.WriteAsync(page, cancellationToken);
            if (chaos.Strikes(0.25))
                throw new InvalidOperationException("The worker died after the write.");
        });
        await host.StartAsync();
        await EnqueueAsync(host);
        await WaitAsync(jobs => jobs.Count == Owners && jobs.All(job => job.Status == JobStatus.Completed));
        await host.StopAsync();

        Assert.True(receiver.DeliveredRecords > receiver.Stored.Count, "Records must have been delivered more than once, or this proves nothing.");
        Assert.Equal(Owners * PagesPerJob * PerPage, receiver.Stored.Count);
        Assert.All(receiver.Stored, row => Assert.Equal(api.NewestServed(row.Key.Owner, row.Key.Key), row.Value.UpdatedAt));
        Assert.Equal(0, receiver.IdsWithTwoBodies);
        Assert.Equal(0, receiver.BadSignatures);
    }

    private async Task NaiveInsertAsync(RecordPage page, CancellationToken cancellationToken)
    {
        await using var source = NpgsqlDataSource.Create(ConnectionString!);
        await using var command = source.CreateCommand(
            $"INSERT INTO {Schema}.orders (owner, key, record) SELECT @owner, r->>'id', r FROM jsonb_array_elements(@page::jsonb) AS r");
        command.Parameters.AddWithValue("owner", page.Job.Owner);
        command.Parameters.AddWithValue("page", page.Records.GetRawText());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnqueueAsync(IHost host)
    {
        var client = host.Services.GetRequiredService<ISyncLane>();
        var enqueued = await Task.WhenAll(Enumerable.Range(1, Owners).Select(owner =>
            client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner($"shop-{owner}"), Type = "items" })));
        Assert.All(enqueued, result => Assert.True(result.Succeeded));
    }

    private async Task WaitAsync(Func<IReadOnlyList<Job>, bool> done)
    {
        using var timeout = new CancellationTokenSource(Patience);

        // Sequential: polling until the run is done.
        while (!done(await JobsAsync()))
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
    }

    private async Task<List<(string Owner, string Key, string? Version)>> RowsAsync()
    {
        await using var source = NpgsqlDataSource.Create(ConnectionString!);
        await using var command = source.CreateCommand($"SELECT owner, key, record->>'updated_at' FROM {Schema}.orders");
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string, string?)>();

        // Sequential: reading rows from one data reader.
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));

        return rows;
    }

    private IHost Host(ChangingApi api, Func<RecordPage, CancellationToken, Task> write)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.PollInterval = TimeSpan.FromMilliseconds(20);
            options.AddQueue(Queue, queue =>
            {
                queue.Concurrency = 3;
                queue.MaxAttempts = 100;
                queue.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
                queue.RetryMaxDelay = TimeSpan.FromMilliseconds(50);
                queue.Handle<HttpRecipeHandler>("items");
            });
        });
        builder.Services.AddSyncLaneHttp(http => http.Recipe("items", """
            {
              "name": "test/items",
              "request": { "url": "https://api.example.com/{{owner.key}}/items" },
              "records": "$.items",
              "pagination": { "type": "page", "param": "page", "size": 4 }
            }
            """).WriteTo(write));
        builder.Services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);
        return builder.Build();
    }

    /// <summary>
    /// A receiver that keeps the contract: it checks the signature, and keeps one record per owner and key, the newer
    /// version winning. It also misbehaves: one answer in eight is lost after the records were stored (500), and one in
    /// eight asks to slow down before storing (503, with <c>Retry-After: 0</c>).
    /// </summary>
    private sealed class ContractReceiver(int seed) : HttpMessageHandler
    {
        public static readonly string Secret = TestSecrets.Webhook;

        private readonly Chaos _chaos = new(seed);
        private readonly ConcurrentDictionary<string, string> _bodies = new();
        private readonly Lock _lock = new();
        private int _deliveredRecords;
        private int _idsWithTwoBodies;
        private int _badSignatures;

        public Dictionary<(string Owner, string Key), (decimal Version, string UpdatedAt)> Stored { get; } = [];

        public int DeliveredRecords => _deliveredRecords;

        public int IdsWithTwoBodies => _idsWithTwoBodies;

        public int BadSignatures => _badSignatures;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var id = request.Headers.GetValues("webhook-id").Single();
            var timestamp = request.Headers.GetValues("webhook-timestamp").Single();
            var signature = request.Headers.GetValues("webhook-signature").Single();
            var key = Convert.FromBase64String(Secret["whsec_".Length..]);
            var expected = "v1," + Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
            if (signature != expected)
                Interlocked.Increment(ref _badSignatures);

            if (_bodies.GetOrAdd(id, body) != body)
                Interlocked.Increment(ref _idsWithTwoBodies);

            if (_chaos.Strikes(0.125))
            {
                var busy = FakeApi.Response(HttpStatusCode.ServiceUnavailable);
                busy.Headers.TryAddWithoutValidation("Retry-After", "0");
                return busy;
            }

            using var document = System.Text.Json.JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            var owner = data.GetProperty("owner").GetString()!;
            lock (_lock)
            {
                foreach (var record in data.GetProperty("records").EnumerateArray())
                {
                    _deliveredRecords++;
                    var version = record.GetProperty("version").GetDecimal();
                    var row = (owner, record.GetProperty("key").GetString()!);
                    if (!Stored.TryGetValue(row, out var stored) || version >= stored.Version)
                        Stored[row] = (version, record.GetProperty("record").GetProperty("updated_at").GetString()!);
                }
            }

            return _chaos.Strikes(0.125) ? FakeApi.Response(HttpStatusCode.InternalServerError) : FakeApi.Response(HttpStatusCode.OK);
        }
    }

    /// <summary>Strikes at random, with a fixed seed so a failure can be run again.</summary>
    private sealed class Chaos(int seed)
    {
        private readonly Random _random = new(seed);
        private readonly Lock _lock = new();
        private int _struck;

        public int Struck => _struck;

        public bool Strikes(double chance)
        {
            lock (_lock)
            {
                if (_random.NextDouble() >= chance)
                    return false;

                _struck++;
                return true;
            }
        }
    }

    /// <summary>
    /// Five pages of four records per owner. Every answer gives each of its records a newer <c>updated_at</c>, as a live
    /// API whose records keep changing would. One answer in five fails with a 500.
    /// </summary>
    private sealed class ChangingApi(int seed) : HttpMessageHandler
    {
        private readonly Chaos _failures = new(seed);
        private readonly ConcurrentDictionary<(string Owner, string Key), string> _newest = new();
        private long _clock;
        private int _failed;

        public int Failed => _failed;

        public string NewestServed(string owner, string key) => _newest[(owner, key)];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_failures.Strikes(0.2))
            {
                Interlocked.Increment(ref _failed);
                return Task.FromResult(FakeApi.Response(HttpStatusCode.InternalServerError));
            }

            var owner = request.RequestUri!.Segments[1].TrimEnd('/');
            var page = int.Parse(request.RequestUri.Query.Split('=')[1], CultureInfo.InvariantCulture);
            var records = new List<string>();
            for (var index = 0; index < (page <= PagesPerJob ? PerPage : 0); index++)
            {
                var key = $"p{page}-r{index}";
                var updated = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _clock)).ToString("O", CultureInfo.InvariantCulture);
                _newest.AddOrUpdate((owner, key), updated, (_, previous) => string.CompareOrdinal(updated, previous) > 0 ? updated : previous);
                records.Add($$"""{ "id": "{{key}}", "updated_at": "{{updated}}" }""");
            }

            return Task.FromResult(FakeApi.Response(HttpStatusCode.OK, $$"""{ "items": [{{string.Join(", ", records)}}] }"""));
        }
    }
}
