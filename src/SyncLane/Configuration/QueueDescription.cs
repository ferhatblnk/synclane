namespace SyncLane;

/// <summary>A declared queue as packages and tools see it: read-only, taken after validation.</summary>
/// <param name="Name">The queue's name.</param>
/// <param name="JobTypes">Its job types, in the order they were declared.</param>
public sealed record QueueDescription(string Name, IReadOnlyList<JobTypeDescription> JobTypes);

/// <summary>A declared job type as packages and tools see it.</summary>
/// <param name="Name">The job type, as written to the <c>type</c> column.</param>
/// <param name="Handler">The handler that runs its pages.</param>
public sealed record JobTypeDescription(string Name, Type Handler);
