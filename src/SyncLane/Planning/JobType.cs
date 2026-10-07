namespace SyncLane.Planning;

/// <summary>A declared job type. Priority is the position of the declaration, so types run in the order they were declared.</summary>
internal sealed record JobType(string Name, int Priority, Type Handler, PlanRule? Rule);
