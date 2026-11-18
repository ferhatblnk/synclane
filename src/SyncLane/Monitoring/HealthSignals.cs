using System.Collections.Concurrent;

namespace SyncLane.Monitoring;

/// <summary>What the background services last managed, kept for the health check: one write per heartbeat or round.</summary>
internal sealed class HealthSignals
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _planFailingSince = new();
    private long _heartbeatAt;

    /// <summary>When this instance last registered or renewed its registration; <c>null</c> before it registered.</summary>
    public DateTimeOffset? HeartbeatAt =>
        Interlocked.Read(ref _heartbeatAt) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>The queues whose latest planning round failed, with when the failing began.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> PlanFailures => _planFailingSince;

    /// <summary>
    /// Why the switch in the database could not turn this instance's workers or planner on; <c>null</c> when nothing
    /// stands in the way.
    /// </summary>
    public string? SwitchRefused { get; private set; }

    public void Refused(string? why) => SwitchRefused = why;

    /// <summary>
    /// The application's name, and the live instances under it that declare none of what this one declares;
    /// <c>null</c> when there are none.
    /// </summary>
    public (string Application, string Instances)? SharedName { get; private set; }

    public void Strangers((string Application, string Instances)? shared) => SharedName = shared;

    public void Heartbeat(DateTimeOffset at) => Interlocked.Exchange(ref _heartbeatAt, at.UtcTicks);

    public void Planned(string queue) => _planFailingSince.TryRemove(queue, out _);

    public void PlanFailed(string queue, DateTimeOffset at) => _planFailingSince.TryAdd(queue, at);
}
