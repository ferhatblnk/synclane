namespace SyncLane;

/// <summary>Which counts to return.</summary>
public sealed record VolumeQuery
{
    /// <summary>The start of the first bucket.</summary>
    public required DateTimeOffset From { get; init; }

    /// <summary>The end, exclusive. Defaults to now.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only this queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Only this job type.</summary>
    public string? Type { get; init; }

    /// <summary>The size of a bucket. Default <see cref="VolumeUnit.Hour"/>.</summary>
    public VolumeUnit Unit { get; init; } = VolumeUnit.Hour;
}

/// <summary>The size of a volume bucket, in UTC.</summary>
public enum VolumeUnit
{
    /// <summary>One hour.</summary>
    Hour,

    /// <summary>One day.</summary>
    Day
}

/// <summary>What happened in one hour or day. Buckets in which nothing happened are left out.</summary>
public sealed record VolumeBucket
{
    /// <summary>The start of the bucket, in UTC.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>Jobs completed.</summary>
    public long Completed { get; init; }

    /// <summary>Records of the jobs completed.</summary>
    public long Records { get; init; }

    /// <summary>Failed attempts, whatever came of them.</summary>
    public long Failures { get; init; }

    /// <summary>Jobs given up.</summary>
    public long Abandoned { get; init; }
}
