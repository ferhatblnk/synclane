using System.Globalization;
using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// Checks that an <see cref="IRecordSink"/> survives what workers do to it: pages written again after a crash, pages
/// fetched again with changed records, duplicates within a page, an older copy arriving after a newer one, and the same
/// key under two owners. Call it from any test; every failed scenario is in the result, named.
/// </summary>
/// <example>
/// <code>
/// var result = await SinkConformance.CheckAsync(new OrderSink(db), owner => db.ReadSamplesAsync(owner));
/// Assert.True(result.Succeeded, string.Join("\n", result.Errors));
/// </code>
/// </example>
public static class SinkConformance
{
    private static readonly DateTimeOffset Earlier = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Earlier.AddHours(1);

    /// <summary>
    /// Writes a few pages to <paramref name="sink"/>, for owners made up for this run, and reads back what it stored after
    /// each one with <paramref name="read"/>.
    /// </summary>
    /// <param name="sink">The sink to check, writing to a store the test can read.</param>
    /// <param name="read">What the store holds for an owner, as samples: only their id and value are compared.</param>
    /// <param name="options">Whether the sink keeps the newer version, and how a sample becomes a record it accepts.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    public static async Task<SyncResult> CheckAsync(
        IRecordSink sink,
        Func<string, CancellationToken, Task<IReadOnlyCollection<SinkSample>>> read,
        SinkConformanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(read);
        options ??= new SinkConformanceOptions();

        var run = Guid.NewGuid().ToString("N")[..12];
        string Owner(string name) => $"conformance-{run}-{name}";
        var errors = new List<SyncError>();
        var check = new Check(sink, read, options, errors, cancellationToken);
        SinkSample[] page = [Sample("1", 1), Sample("2", 1), Sample("3", 1)];

        // Each scenario has owners of its own, so one that fails does not make the next ones fail too.
        await check.ScenarioAsync("a page written twice",
            [(Owner("twice"), page), (Owner("twice"), page)],
            (Owner("twice"), new() { ["1"] = 1, ["2"] = 1, ["3"] = 1 }));

        await check.ScenarioAsync("a page fetched again with one record changed and one added",
            [(Owner("again"), page), (Owner("again"), [Sample("1", 1), Sample("2", 2, Later), Sample("4", 1)])],
            (Owner("again"), new() { ["1"] = 1, ["2"] = 2, ["3"] = 1, ["4"] = 1 }));

        await check.ScenarioAsync("two copies of a record in one page",
            [(Owner("copies"), [Sample("5", 1), Sample("5", 2)])],
            (Owner("copies"), new() { ["5"] = 2 }));

        if (options.Versioned)
        {
            await check.ScenarioAsync("an older copy written after a newer one",
                [(Owner("older"), [Sample("6", 2, Later)]), (Owner("older"), [Sample("6", 1)])],
                (Owner("older"), new() { ["6"] = 2 }));
        }

        await check.ScenarioAsync("the same key under another owner",
            [(Owner("first"), [Sample("1", 1)]), (Owner("second"), [Sample("1", 9)])],
            (Owner("second"), new() { ["1"] = 9 }), (Owner("first"), new() { ["1"] = 1 }));

        await check.ScenarioAsync("an empty page",
            [(Owner("empty"), [])],
            (Owner("empty"), []));

        return errors.Count == 0 ? SyncResult.Success() : SyncResult.Failure(errors);
    }

    private static SinkSample Sample(string id, int value, DateTimeOffset? updatedAt = null) => new(id, value, updatedAt ?? Earlier);

    /// <summary>The default record of a sample: <c>{ "id": "1", "value": 1, "updated_at": "2026-01-01T12:00:00Z" }</c>.</summary>
    private static string DefaultRecord(SinkSample sample)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", sample.Id);
            writer.WriteNumber("value", sample.Value);
            writer.WriteString("updated_at", sample.UpdatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class Check(
        IRecordSink sink,
        Func<string, CancellationToken, Task<IReadOnlyCollection<SinkSample>>> read,
        SinkConformanceOptions options,
        List<SyncError> errors,
        CancellationToken cancellationToken)
    {
        public async Task ScenarioAsync(string scenario, (string Owner, SinkSample[] Records)[] writes, params (string Owner, Dictionary<string, int> Expected)[] checks)
        {
            try
            {
                // Sequential: the pages of a scenario are written in order; that order is the scenario.
                foreach (var (owner, records) in writes)
                {
                    var json = "[" + string.Join(",", records.Select(options.Record ?? DefaultRecord)) + "]";
                    using var document = JsonDocument.Parse(json);
                    var job = new Job { Id = 1, Queue = "conformance", Owner = owner, Lane = owner, Type = "conformance", Mode = JobMode.Initial, Status = JobStatus.Running };
                    await sink.WriteAsync(new RecordPage(job, "conformance", document.RootElement), cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(HttpErrors.SinkNotIdempotent(scenario, $"the sink threw {exception.GetType().Name}"));
                return;
            }

            string? finding = null;

            // Sequential: one owner read after the other; two at most.
            foreach (var (owner, expected) in checks)
            {
                if (finding is null && Compare(await read(owner, cancellationToken), expected) is { } found)
                    finding = owner == checks[0].Owner ? found : $"the other owner's records changed: {found}";
            }

            if (finding is not null)
                errors.Add(HttpErrors.SinkNotIdempotent(scenario, finding));
        }

        private static string? Compare(IReadOnlyCollection<SinkSample> stored, Dictionary<string, int> expected)
        {
            var twice = stored.GroupBy(sample => sample.Id).Where(group => group.Count() > 1).Select(group => group.Key).Order().ToList();
            if (twice.Count > 0)
                return $"record {string.Join(", ", twice)} is stored more than once";

            var missing = expected.Keys.Where(id => stored.All(sample => sample.Id != id)).Order().ToList();
            if (missing.Count > 0)
                return $"record {string.Join(", ", missing)} is missing";

            var wrong = stored.Where(sample => expected.TryGetValue(sample.Id, out var value) && value != sample.Value)
                .Select(sample => $"record {sample.Id} holds {sample.Value} instead of {expected[sample.Id]}")
                .ToList();
            if (wrong.Count > 0)
                return string.Join("; ", wrong);

            var extra = stored.Where(sample => !expected.ContainsKey(sample.Id)).Select(sample => sample.Id).Order().ToList();
            return extra.Count > 0 ? $"record {string.Join(", ", extra)} should not be there" : null;
        }
    }
}

/// <summary>How <see cref="SinkConformance"/> treats a sink.</summary>
public sealed class SinkConformanceOptions
{
    /// <summary>The sink keeps the newer version of a record, so an older copy written later must not replace it.</summary>
    public bool Versioned { get; init; }

    /// <summary>
    /// How a sample becomes a record the sink accepts, as JSON. Default <c>{ "id", "value", "updated_at" }</c>. A sink
    /// that expects its own fields, such as <c>total</c>, maps the sample's value onto one of them.
    /// </summary>
    public Func<SinkSample, string>? Record { get; init; }
}

/// <summary>A record of the conformance check: its id, a value that changes, and when it last changed.</summary>
/// <param name="Id">The record's id.</param>
/// <param name="Value">A value that changes between copies of the record.</param>
/// <param name="UpdatedAt">When the record last changed. Not compared when reading back.</param>
public readonly record struct SinkSample(string Id, int Value, DateTimeOffset UpdatedAt = default);
