namespace SyncLane.Planning;

/// <summary>Describes the jobs an owner should have for one type.</summary>
internal abstract record PlanRule(TimeSpan? Every, TimeSpan Offset)
{
    public virtual void Validate(string queue, string type)
    {
        Require(Every is null || Every > TimeSpan.Zero, queue, type, "every must be positive");
        Require(Offset >= TimeSpan.Zero && (Every is null || Offset < Every), queue, type, "offset must be zero or more and shorter than every");
    }

    protected static void Require(bool condition, string queue, string type, string rule)
    {
        if (!condition)
            throw new SyncLaneException(SyncLaneErrors.JobTypeSettingInvalid(queue, type, rule));
    }
}
