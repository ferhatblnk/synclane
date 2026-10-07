namespace SyncLane;

/// <summary>Why a job exists. Stored in lower case in the <c>mode</c> column.</summary>
public enum JobMode
{
    /// <summary>Part of the first load of an owner: one snapshot per type and the slices of history.</summary>
    Initial,

    /// <summary>A periodic refresh, planned once per period.</summary>
    Recurring,

    /// <summary>Enqueued by hand, outside the plan.</summary>
    Manual
}
