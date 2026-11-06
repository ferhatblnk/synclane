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

    public void Heartbeat(DateTimeOffset at) => Interlocked.Exchange(ref _heartbeatAt, at.UtcTicks);

    public void Planned(string queue) => _planFailingSince.TryRemove(queue, out _);

    public void PlanFailed(string queue, DateTimeOffset at) => _planFailingSince.TryAdd(queue, at);
}
