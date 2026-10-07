namespace SyncLane.Planning;

/// <summary>What already exists for one owner and one type. Coverage counts every planned job, whatever its status.</summary>
/// <param name="HasInitial">An initial job exists.</param>
/// <param name="InitialFrom">The oldest point the initial windows reach back to.</param>
/// <param name="CoveredTo">The newest point any window reaches.</param>
/// <param name="AnyOutstanding">A job of the type has not reached an end yet.</param>
/// <param name="RecurringOutstanding">A recurring job of the type has not reached an end yet.</param>
/// <param name="LastPlannedAt">When the newest job of the type was created.</param>
/// <param name="LastRecurringAt">When the newest recurring job of the type was created.</param>
internal sealed record TypeCoverage(
    bool HasInitial,
    DateTimeOffset? InitialFrom,
    DateTimeOffset? CoveredTo,
    bool AnyOutstanding,
    bool RecurringOutstanding,
    DateTimeOffset? LastPlannedAt,
    DateTimeOffset? LastRecurringAt)
{
    public static readonly TypeCoverage None = new(false, null, null, false, false, null, null);
}
