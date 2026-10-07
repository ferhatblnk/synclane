namespace SyncLane.Tests.Support;

/// <summary>Counts how many jobs are at work at once, per lane and overall.</summary>
public sealed class LaneProbe
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _atWork = [];
    private int _overall;

    public int MostPerLane { get; private set; }

    public int MostOverall { get; private set; }

    public void Enter(string lane)
    {
        lock (_gate)
        {
            var count = _atWork.GetValueOrDefault(lane) + 1;
            _atWork[lane] = count;
            _overall++;
            MostPerLane = Math.Max(MostPerLane, count);
            MostOverall = Math.Max(MostOverall, _overall);
        }
    }

    public void Leave(string lane)
    {
        lock (_gate)
        {
            _atWork[lane]--;
            _overall--;
        }
    }
}
