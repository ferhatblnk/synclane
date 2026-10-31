namespace SyncLane;

/// <summary>A declared rate budget as packages and tools see it: read-only, taken after validation.</summary>
/// <param name="Name">The budget's name.</param>
/// <param name="Limit">How many requests per <paramref name="Per"/>, at a steady pace.</param>
/// <param name="Per">The period of <paramref name="Limit"/>.</param>
/// <param name="Burst">How many requests may go at once after a quiet spell.</param>
/// <param name="Start">The share of <paramref name="Limit"/> a new bucket starts at, from 0.01 to 1.</param>
public sealed record BudgetDescription(string Name, int Limit, TimeSpan Per, int Burst, double Start = 1);
