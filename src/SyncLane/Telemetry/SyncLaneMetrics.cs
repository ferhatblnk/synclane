using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SyncLane.Telemetry;

/// <summary>
/// The instruments of the <see cref="SyncLaneTelemetry.MeterName"/> meter. Tags carry the queue and the job type only.
/// Owners and lanes are left out on purpose: there can be thousands of them, and they often identify customers.
/// </summary>
internal sealed class SyncLaneMetrics : IDisposable
{
    public const string QueueTag = "synclane.queue";
    public const string TypeTag = "synclane.job.type";
    public const string OutcomeTag = "synclane.job.outcome";
    public const string BudgetTag = "synclane.budget";

    private readonly bool _ownsMeter;
    private readonly Counter<long> _taken;
    private readonly Histogram<double> _wait;
    private readonly Counter<long> _finished;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _pages;
    private readonly Histogram<double> _pageDuration;
    private readonly Counter<long> _records;
    private readonly Counter<long> _throttled;
    private readonly Counter<long> _planned;
    private readonly Counter<long> _planFailures;
    private readonly Histogram<double> _budgetWait;
    private readonly Counter<long> _budgetPaused;

    public SyncLaneMetrics(IMeterFactory? factory = null)
    {
        _ownsMeter = factory is null;
        Meter = factory?.Create(SyncLaneTelemetry.MeterName, SyncLaneTelemetry.Version)
            ?? new Meter(SyncLaneTelemetry.MeterName, SyncLaneTelemetry.Version);

        _taken = Meter.CreateCounter<long>("synclane.job.taken", "{job}", "Jobs a worker took.");
        _wait = Meter.CreateHistogram<double>("synclane.job.wait", "s", "Time between a job becoming due and a worker taking it.");
        _finished = Meter.CreateCounter<long>("synclane.job.finished", "{job}", "Runs of a job that ended, by outcome.");
        _duration = Meter.CreateHistogram<double>("synclane.job.duration", "s", "Time from taking a job to the end of the run, by outcome.");
        _pages = Meter.CreateCounter<long>("synclane.page.completed", "{page}", "Pages a handler returned.");
        _pageDuration = Meter.CreateHistogram<double>("synclane.page.duration", "s", "Time a handler took for a page it returned.");
        _records = Meter.CreateCounter<long>("synclane.record.processed", "{record}", "Records the handlers reported as processed.");
        _throttled = Meter.CreateCounter<long>("synclane.job.throttled", "{job}", "Runs that ended because the other side asked to slow down.");
        _planned = Meter.CreateCounter<long>("synclane.planner.planned", "{job}", "Jobs the planner added.");
        _planFailures = Meter.CreateCounter<long>("synclane.planner.failed", "{run}", "Planner runs that failed.");
        _budgetWait = Meter.CreateHistogram<double>("synclane.budget.wait", "s", "Time a request waited for a token of a rate budget.");
        _budgetPaused = Meter.CreateCounter<long>("synclane.budget.paused", "{pause}", "Times a rate budget was paused because the API asked to slow down.");
    }

    public Meter Meter { get; }

    public void Taken(string queue, string type, TimeSpan wait)
    {
        var tags = Tags(queue, type);
        _taken.Add(1, tags);
        _wait.Record(Math.Max(wait.TotalSeconds, 0), tags);
    }

    public void Finished(string queue, string type, string outcome, TimeSpan duration)
    {
        var tags = Tags(queue, type);
        tags.Add(OutcomeTag, outcome);
        _finished.Add(1, tags);
        _duration.Record(duration.TotalSeconds, tags);
    }

    public void Page(string queue, string type, int processed, TimeSpan duration)
    {
        var tags = Tags(queue, type);
        _pages.Add(1, tags);
        _pageDuration.Record(duration.TotalSeconds, tags);
        _records.Add(processed, tags);
    }

    public void Throttled(string queue, string type) => _throttled.Add(1, Tags(queue, type));

    public void Planned(string queue, int jobs) => _planned.Add(jobs, new KeyValuePair<string, object?>(QueueTag, queue));

    public void PlanFailed(string queue) => _planFailures.Add(1, new KeyValuePair<string, object?>(QueueTag, queue));

    /// <summary>Tagged with the budget's name only: its key is an owner, which never goes into a tag.</summary>
    public void BudgetWaited(string budget, TimeSpan wait) =>
        _budgetWait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>(BudgetTag, budget));

    public void BudgetPaused(string budget) => _budgetPaused.Add(1, new KeyValuePair<string, object?>(BudgetTag, budget));

    public void Dispose()
    {
        if (_ownsMeter)
            Meter.Dispose();
    }

    private static TagList Tags(string queue, string type) => new() { { QueueTag, queue }, { TypeTag, type } };
}
