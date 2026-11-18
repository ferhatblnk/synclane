namespace SyncLane.Instances;

/// <summary>
/// What an operator set for an application and role. A <c>null</c> value leaves the application's own setting in place.
/// </summary>
internal sealed record SwitchRow(bool? RunWorkers, bool? RunPlanner, bool? Listen);
