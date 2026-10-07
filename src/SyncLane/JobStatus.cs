namespace SyncLane;

/// <summary>Where a job is in its life. Stored in lower case in the <c>status</c> column.</summary>
public enum JobStatus
{
    /// <summary>Waiting for a worker.</summary>
    Pending,

    /// <summary>A worker holds the job.</summary>
    Running,

    /// <summary>Finished.</summary>
    Completed,

    /// <summary>The last attempt failed and the job will run again after its delay.</summary>
    Failed,

    /// <summary>Out of regular attempts. It gets one more, once nothing else is waiting in its lane.</summary>
    Deferred,

    /// <summary>Given up. It stays this way until someone requeues it.</summary>
    Abandoned
}
