using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncLane.Http;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

public class SinkConformanceTests
{
    [Fact]
    public async Task A_sink_that_writes_by_key_and_keeps_the_newer_version_passes_every_scenario()
    {
        var sink = new MemorySink();

        var result = await SinkConformance.CheckAsync(sink, sink.ReadAsync, new SinkConformanceOptions { Versioned = true });

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
    }

    [Theory]
    [InlineData("plain insert", "a page written twice", "record 1, 2, 3 is stored more than once")]
    [InlineData("never updates", "a page fetched again with one record changed and one added", "record 2 holds 1 instead of 2")]
    [InlineData("ignores owners", "the same key under another owner", "the other owner's records changed")]
    [InlineData("fails on empty", "an empty page", "the sink threw InvalidOperationException")]
    public async Task A_sink_that_breaks_on_replays_fails_the_scenario_that_shows_it(string flaw, string scenario, string finding)
    {
        var sink = flaw switch
        {
            "plain insert" => new MemorySink(keyed: false),
            "never updates" => new MemorySink(updates: false),
            "ignores owners" => new MemorySink(byOwner: false),
            _ => new MemorySink(failsOnEmpty: true)
        };

        var result = await SinkConformance.CheckAsync(sink, sink.ReadAsync);

        var error = Assert.Single(result.Errors, error => error.Message.Contains($"'{scenario}'"));
        Assert.Equal(HttpErrorCodes.SinkNotIdempotent, error.Code);
        Assert.Contains(finding, error.Message);
    }

    [Fact]
    public async Task A_sink_that_ignores_versions_passes_without_them_and_fails_with_them()
    {
        var sink = new MemorySink(versions: false);

        var without = await SinkConformance.CheckAsync(sink, sink.ReadAsync);
        var with = await SinkConformance.CheckAsync(sink, sink.ReadAsync, new SinkConformanceOptions { Versioned = true });

        Assert.True(without.Succeeded, string.Join('\n', without.Errors));
        Assert.Contains(with.Errors, error => error.Message.Contains("'an older copy written after a newer one'") && error.Message.Contains("record 6 holds 1 instead of 2"));
    }

    [Fact]
    public async Task A_sink_with_its_own_record_shape_gets_samples_in_that_shape()
    {
        var sink = new MemorySink(idField: "order_id", valueField: "total");

        var result = await SinkConformance.CheckAsync(sink, sink.ReadAsync, new SinkConformanceOptions
        {
            Record = sample => $$"""{ "order_id": "{{sample.Id}}", "total": {{sample.Value}}, "updated_at": "{{sample.UpdatedAt:O}}" }"""
        });

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
    }

    /// <summary>A store in memory, written correctly unless told to make one of the usual mistakes.</summary>
    private sealed class MemorySink(
        bool keyed = true, bool updates = true, bool versions = true, bool byOwner = true, bool failsOnEmpty = false,
        string idField = "id", string valueField = "value") : IRecordSink
    {
        private readonly List<(string Owner, string Id, int Value, DateTimeOffset UpdatedAt)> _rows = [];

        public Task WriteAsync(RecordPage page, CancellationToken cancellationToken)
        {
            if (failsOnEmpty && page.Count == 0)
                throw new InvalidOperationException("Nothing to write.");

            foreach (var record in page.Records.EnumerateArray())
            {
                var row = (page.Job.Owner, record.GetProperty(idField).GetString()!, record.GetProperty(valueField).GetInt32(),
                    DateTimeOffset.Parse(record.GetProperty("updated_at").GetString()!, CultureInfo.InvariantCulture));
                var index = _rows.FindIndex(existing => existing.Id == row.Item2 && (!byOwner || existing.Owner == row.Owner));
                if (!keyed || index < 0)
                    _rows.Add(row);
                else if (updates && (!versions || row.Item4 >= _rows[index].UpdatedAt))
                    _rows[index] = row;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<SinkSample>> ReadAsync(string owner, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<SinkSample>>(_rows.Where(row => row.Owner == owner).Select(row => new SinkSample(row.Id, row.Value)).ToList());
    }
}

/// <summary>The built-in destinations pass the check they offer.</summary>
public class BuiltInSinkConformanceTests : PostgresTest
{
    [PostgresFact]
    public async Task The_postgres_destination_passes_every_scenario()
    {
        await using var sinks = new PostgresSinks(new SyncLaneOptions { ConnectionString = ConnectionString!, Schema = Schema }, TimeProvider.System, NullLoggerFactory.Instance);
        // Declared: an inferred $.id refuses a page with two records of one id, rather than guess that they are one.
        var options = new PostgresSinkOptions("samples").KeyedBy("$.id").VersionedBy("$.updated_at");
        Assert.Empty(options.Validate("samples"));

        var result = await SinkConformance.CheckAsync(sinks.For(options), ReadAsync, new SinkConformanceOptions { Versioned = true });

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
    }

    [PostgresFact]
    public async Task The_http_destination_with_a_receiver_that_keeps_the_contract_passes_every_scenario()
    {
        var receiver = new KeyedReceiver();
        var options = new HttpSinkOptions("https://receiver.example.com/sync").KeyedBy("$.id").VersionedBy("$.updated_at");
        Assert.Empty(options.Validate("samples"));
        var services = new ServiceCollection();
        services.AddHttpClient(HttpSource.DestinationClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
        await using var provider = services.BuildServiceProvider();
        var sink = new HttpSinks(provider.GetRequiredService<IHttpClientFactory>(), new HttpSourceOptions(), TimeProvider.System).For(options);

        var result = await SinkConformance.CheckAsync(sink, receiver.ReadAsync, new SinkConformanceOptions { Versioned = true });

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
    }

    private async Task<IReadOnlyCollection<SinkSample>> ReadAsync(string owner, CancellationToken cancellationToken)
    {
        await using var source = NpgsqlDataSource.Create(ConnectionString!);
        await using var command = source.CreateCommand($"SELECT key, (record->>'value')::int FROM {Schema}.samples WHERE owner = @owner");
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var samples = new List<SinkSample>();

        // Sequential: reading rows from one data reader.
        while (await reader.ReadAsync(cancellationToken))
            samples.Add(new SinkSample(reader.GetString(0), reader.GetInt32(1)));

        return samples;
    }

    /// <summary>A receiver that keeps one record per owner and key, the version not lower winning.</summary>
    private sealed class KeyedReceiver : HttpMessageHandler
    {
        private readonly Dictionary<(string Owner, string Key), (decimal? Version, int Value)> _rows = [];

        public Task<IReadOnlyCollection<SinkSample>> ReadAsync(string owner, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<SinkSample>>(_rows.Where(row => row.Key.Owner == owner).Select(row => new SinkSample(row.Key.Key, row.Value.Value)).ToList());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var data = document.RootElement.GetProperty("data");
            var owner = data.GetProperty("owner").GetString()!;
            foreach (var record in data.GetProperty("records").EnumerateArray())
            {
                var version = record.GetProperty("version").ValueKind == JsonValueKind.Number ? record.GetProperty("version").GetDecimal() : (decimal?)null;
                var key = (owner, record.GetProperty("key").GetString()!);
                if (!_rows.TryGetValue(key, out var stored) || stored.Version is null || version >= stored.Version)
                    _rows[key] = (version, record.GetProperty("record").GetProperty("value").GetInt32());
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
