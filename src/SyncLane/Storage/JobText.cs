namespace SyncLane.Storage;

/// <summary>The lower-case names statuses and modes have in the table.</summary>
internal static class JobText
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Deferred = "deferred";
    public const string Abandoned = "abandoned";

    public const string Initial = "initial";
    public const string Recurring = "recurring";
    public const string Manual = "manual";

    public static string Of(JobStatus status) => status switch
    {
        JobStatus.Pending => Pending,
        JobStatus.Running => Running,
        JobStatus.Completed => Completed,
        JobStatus.Failed => Failed,
        JobStatus.Deferred => Deferred,
        JobStatus.Abandoned => Abandoned,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string Of(JobMode mode) => mode switch
    {
        JobMode.Initial => Initial,
        JobMode.Recurring => Recurring,
        JobMode.Manual => Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static JobStatus Status(string text) => text switch
    {
        Pending => JobStatus.Pending,
        Running => JobStatus.Running,
        Completed => JobStatus.Completed,
        Failed => JobStatus.Failed,
        Deferred => JobStatus.Deferred,
        Abandoned => JobStatus.Abandoned,
        _ => throw new SyncLaneException(SyncLaneErrors.UnknownStatus(text))
    };

    public static JobMode Mode(string text) => text switch
    {
        Initial => JobMode.Initial,
        Recurring => JobMode.Recurring,
        Manual => JobMode.Manual,
        _ => throw new SyncLaneException(SyncLaneErrors.UnknownMode(text))
    };
}
