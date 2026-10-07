namespace SyncLane.Tests.Support;

/// <summary>Remembers which handler instances ran.</summary>
public sealed class ScopeLog
{
    public List<object> Instances { get; } = [];
}
