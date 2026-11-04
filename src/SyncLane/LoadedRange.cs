namespace SyncLane;

/// <summary>A range of history that another system already loaded for one owner and job type.</summary>
public sealed record LoadedRange
{
    /// <summary>Who the history belongs to. Its lane is the one its jobs run in.</summary>
    public required JobOwner Owner { get; init; }

    /// <summary>The job type. It must be declared with <c>Windowed</c> on the queue.</summary>
    public required string Type { get; init; }

    /// <summary>The oldest moment the loaded history covers.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>
    /// The newest moment the loaded history covers. It must not be in the future. For a type without <c>every</c> it must
    /// reach today (UTC), because such a type never loads what came after its history.
    /// </summary>
    public required DateTimeOffset End { get; init; }
}
