namespace SyncLane;

/// <summary>
/// A token taken from a rate budget by <see cref="RateBudgets.WaitAsync"/>: permission to send one request. Keep it until
/// the answer arrives; if the API answers "slow down", pass it to <see cref="RateBudgets.ThrottledAsync"/>.
/// </summary>
/// <remarks>
/// The permit remembers which pause of the budget it was taken in. A "slow down" answered to a request sent before the
/// latest pause began is already covered by that pause, so it does not make the pause longer: several requests in
/// flight at once that all hear "slow down" pause the budget once, not once each.
/// </remarks>
public sealed class BudgetPermit
{
    internal BudgetPermit(string budget, string key, long generation)
    {
        Budget = budget;
        Key = key;
        Generation = generation;
    }

    /// <summary>The budget's name.</summary>
    public string Budget { get; }

    /// <summary>Whose bucket the token came from.</summary>
    public string Key { get; }

    /// <summary>How many pauses the bucket had when the token was taken.</summary>
    internal long Generation { get; }

    /// <summary>Names the budget only: the key is often an owner, which does not belong in logs.</summary>
    public override string ToString() => $"Permit of budget {Budget}";
}
