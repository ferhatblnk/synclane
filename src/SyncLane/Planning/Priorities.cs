namespace SyncLane.Planning;

/// <summary>
/// The number stored in a job's <c>priority</c> column, lower first: a level's band plus the job type's place in its
/// queue. Normal's band is 0, so rows written before levels existed read as normal. An operator moving an owner forward
/// subtracts <see cref="Boost"/> once, which puts it ahead of every level but urgent.
/// </summary>
internal static class Priorities
{
    /// <summary>The distance between levels; more job types than this in one queue would mix their order.</summary>
    private const int Step = 100_000;

    /// <summary>What an operator's "prioritize" subtracts.</summary>
    public const int Boost = 1_000_000;

    private const int UrgentBand = -10_000_000;

    /// <summary>Jobs at or above this may still be moved forward: not yet moved, and not urgent.</summary>
    public const int Movable = -Boost / 2;

    public static int Of(JobPriority level, int typeOrder) => level switch
    {
        JobPriority.Low => Step + typeOrder,
        JobPriority.Normal => typeOrder,
        JobPriority.High => -Step + typeOrder,
        JobPriority.Urgent => UrgentBand + typeOrder,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public static JobPriority LevelOf(int stored)
    {
        if (stored <= UrgentBand / 2)
            return JobPriority.Urgent;

        var unmoved = IsMoved(stored) ? stored + Boost : stored;
        return unmoved >= Step / 2 ? JobPriority.Low : unmoved <= -Step / 2 ? JobPriority.High : JobPriority.Normal;
    }

    public static bool IsMoved(int stored) => stored is < Movable and > UrgentBand / 2;
}
