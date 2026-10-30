using System.Diagnostics;

namespace SyncLane;

/// <summary>
/// The names SyncLane reports under. With OpenTelemetry:
/// <c>.WithMetrics(m =&gt; m.AddMeter(SyncLaneTelemetry.MeterName))</c> and
/// <c>.WithTracing(t =&gt; t.AddSource(SyncLaneTelemetry.ActivitySourceName))</c>.
/// Without a listener nothing is recorded and nothing is allocated.
/// </summary>
public static class SyncLaneTelemetry
{
    /// <summary>The <see cref="System.Diagnostics.Metrics.Meter"/> that carries every SyncLane metric.</summary>
    public const string MeterName = "SyncLane";

    /// <summary>The <see cref="ActivitySource"/> of the job and page spans. HTTP calls a handler makes become children of the page span.</summary>
    public const string ActivitySourceName = "SyncLane";

    internal static readonly string? Version = typeof(SyncLaneTelemetry).Assembly.GetName().Version?.ToString();

    internal static readonly ActivitySource Source = new(ActivitySourceName, Version);
}
