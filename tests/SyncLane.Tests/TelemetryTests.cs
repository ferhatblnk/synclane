using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SyncLane.Running;
using SyncLane.Telemetry;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class TelemetryTests : PostgresTest
{
    private const string Worker = "w";

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_completed_job_reports_its_pages_records_and_outcome()
    {
        using var metrics = new SyncLaneMetrics();
        using var recorded = new Recorded(metrics);
        var handler = new ScriptedHandler((job, _) => Task.FromResult(job.Cursor switch
        {
            null => new JobPage("2", 100),
            "2" => new JobPage("3", 100),
            _ => new JobPage(null, 40)
        }));

        await RunAsync(metrics, handler);

        Assert.Equal(1, recorded.Sum("synclane.job.taken"));
        Assert.Equal(3, recorded.Sum("synclane.page.completed"));
        Assert.Equal(240, recorded.Sum("synclane.record.processed"));
        Assert.Equal(3, recorded.Count("synclane.page.duration"));
        var finished = Assert.Single(recorded.Of("synclane.job.finished"));
        Assert.Equal(JobOutcome.Completed, finished.Tags[SyncLaneMetrics.OutcomeTag]);
        Assert.Equal((Queue, "orders"), (finished.Tags[SyncLaneMetrics.QueueTag], finished.Tags[SyncLaneMetrics.TypeTag]));
        Assert.Equal(1, recorded.Count("synclane.job.duration"));
    }

    [PostgresFact]
    public async Task A_throttled_run_is_counted_as_failed_and_as_throttled()
    {
        using var metrics = new SyncLaneMetrics();
        using var recorded = new Recorded(metrics);
        var handler = new ScriptedHandler((_, _) => throw new JobThrottledException("429 Too Many Requests", TimeSpan.FromSeconds(30)));

        await RunAsync(metrics, handler);

        Assert.Equal(1, recorded.Sum("synclane.job.throttled"));
        Assert.Equal(JobOutcome.Failed, Assert.Single(recorded.Of("synclane.job.finished")).Tags[SyncLaneMetrics.OutcomeTag]);
        Assert.Equal(0, recorded.Sum("synclane.page.completed"));
    }

    [PostgresFact]
    public async Task No_tag_names_the_owner_or_the_lane()
    {
        using var metrics = new SyncLaneMetrics();
        using var recorded = new Recorded(metrics);

        await RunAsync(metrics, new ScriptedHandler((_, _) => Task.FromResult(new JobPage(null, 1))));

        Assert.All(recorded.All, measurement => Assert.DoesNotContain("customer-42", measurement.Tags.Values.Select(value => value?.ToString())));
    }

    [PostgresFact]
    public async Task A_handler_runs_inside_a_page_span_whose_parent_is_the_job_span()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SyncLaneTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        Activity? seen = null;
        var handler = new ScriptedHandler((_, _) =>
        {
            seen = Activity.Current;
            return Task.FromResult(new JobPage(null, 7));
        });

        using var metrics = new SyncLaneMetrics();
        var job = await RunAsync(metrics, handler);

        Assert.NotNull(seen);
        Assert.Equal("synclane.page", seen.OperationName);
        Assert.Equal("synclane.job", seen.Parent?.OperationName);
        Assert.Equal(job.Id, seen.Parent?.GetTagItem("synclane.job.id"));
        Assert.Equal(JobOutcome.Completed, seen.Parent?.GetTagItem(SyncLaneMetrics.OutcomeTag));
    }

    private async Task<Job> RunAsync(SyncLaneMetrics metrics, ScriptedHandler handler)
    {
        await EnqueueAsync(Noon, Planned("customer-42", key: "1"));
        var leased = await LeaseAsync(Worker, _clock.GetUtcNow()) ?? throw new InvalidOperationException("No job is due.");

        var services = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
        var runner = new JobRunner(Store, services.GetRequiredService<IServiceScopeFactory>(), _clock, metrics, NullLogger<JobRunner>.Instance);
        var queue = new SyncLaneOptions().AddQueue(Queue, options => options.Snapshot<ScriptedHandler>("orders")).Queue(Queue);

        await runner.RunAsync(queue, leased, Worker, CancellationToken.None);
        return await JobAsync(leased.Id);
    }

    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Collects what one <see cref="SyncLaneMetrics"/> instance records, and nothing from other tests.</summary>
    private sealed class Recorded : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();

        public Recorded(SyncLaneMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == metrics.Meter)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public IReadOnlyCollection<Measurement> All => _measurements;

        public IEnumerable<Measurement> Of(string instrument) => _measurements.Where(measurement => measurement.Instrument == instrument);

        public double Sum(string instrument) => Of(instrument).Sum(measurement => measurement.Value);

        public int Count(string instrument) => Of(instrument).Count();

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;

            _measurements.Enqueue(new Measurement(instrument.Name, value, copy));
        }
    }
}
