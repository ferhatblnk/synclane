namespace SyncLane;

/// <summary>
/// The switch of one application and role, and what its live instances run. Listed for every switch that was set and for
/// every application and role a live instance runs under, so an application with no switch shows up too.
/// </summary>
public sealed record SwitchInfo
{
    /// <summary>The application, as its instances register.</summary>
    public required string Application { get; init; }

    /// <summary>The role, from <see cref="SyncLaneOptions.Role"/>; empty for instances that set none.</summary>
    public required string Role { get; init; }

    /// <summary>Whether the switch runs the workers; <c>null</c> leaves it to the application's <c>RunWorkers</c>.</summary>
    public bool? RunWorkers { get; init; }

    /// <summary>Whether the switch runs the planner; <c>null</c> leaves it to the application's <c>RunPlanner</c>.</summary>
    public bool? RunPlanner { get; init; }

    /// <summary>Who set the switch; <c>null</c> when none was set.</summary>
    public string? By { get; init; }

    /// <summary>Why it was set; <c>null</c> when none was set.</summary>
    public string? Reason { get; init; }

    /// <summary>When it was last set; <c>null</c> when none was set.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>Live instances of the application and role.</summary>
    public int LiveInstances { get; init; }

    /// <summary>How many of them run their workers now.</summary>
    public int RunningWorkers { get; init; }

    /// <summary>How many of them run the planner now.</summary>
    public int RunningPlanner { get; init; }
}
