namespace SyncLane;

/// <summary>What <see cref="ISyncLane.EnqueueOrReplaceAsync"/> did with a job.</summary>
/// <param name="Id">The job: the new one, or the one that was there and took the new request's place.</param>
/// <param name="Replaced"><c>true</c> when a job with the same owner, type and dedup key was there already.</param>
public sealed record EnqueuedJob(long Id, bool Replaced);
