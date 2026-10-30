namespace SyncLane.Telemetry;

/// <summary>How a run of a job ended, as reported in the <c>synclane.job.outcome</c> tag.</summary>
internal static class JobOutcome
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Deferred = "deferred";
    public const string Abandoned = "abandoned";
    public const string Released = "released";
    public const string Postponed = "postponed";
    public const string LeaseLost = "lease_lost";
}
