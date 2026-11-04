namespace SyncLane.Storage;

/// <summary>What a worker learns when it saves a page or renews its lease.</summary>
internal enum Hold
{
    /// <summary>Another worker took the job over, or it was deleted: stop without writing.</summary>
    Lost,

    /// <summary>The job is still this worker's.</summary>
    Held,

    /// <summary>The job is still this worker's, but a pause now covers it: hand it back.</summary>
    Paused
}
