namespace SyncLane;

/// <summary>How far the first load of an owner has come, counted from its <see cref="JobMode.Initial"/> jobs.</summary>
/// <param name="Total">Initial jobs planned for the owner.</param>
/// <param name="Completed">Initial jobs that finished.</param>
/// <param name="Outstanding">Initial jobs that are pending, running, waiting for a retry or deferred.</param>
/// <param name="Abandoned">Initial jobs that were given up and need a requeue.</param>
public sealed record OwnerProgress(int Total, int Completed, int Outstanding, int Abandoned)
{
    /// <summary>The first load is still going on.</summary>
    public bool IsLoading => Outstanding > 0;

    /// <summary>Every initial job finished.</summary>
    public bool IsComplete => Total > 0 && Completed == Total;
}
