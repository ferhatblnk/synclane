namespace SyncLane;

/// <summary>
/// How soon a job runs compared with the other due jobs of its queue. Within a level, job types run in the order they
/// were declared. A job never interrupts one already running in its lane: an urgent job runs next, once that one ends.
/// </summary>
public enum JobPriority
{
    /// <summary>After every normal job, for work that can wait, such as a backfill.</summary>
    Low = -1,

    /// <summary>The default.</summary>
    Normal = 0,

    /// <summary>Before normal jobs, for example a new customer's first load.</summary>
    High = 1,

    /// <summary>Before every other job, owners an operator moved forward included.</summary>
    Urgent = 2
}
