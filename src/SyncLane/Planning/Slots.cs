namespace SyncLane.Planning;

/// <summary>The date arithmetic of planning. Everything is anchored to fixed points in UTC, never to the time of the call.</summary>
internal static class Slots
{
    /// <summary>Midnight UTC of the day <paramref name="now"/> falls on.</summary>
    public static DateTimeOffset Day(DateTimeOffset now) =>
        new(now.UtcDateTime.Date, TimeSpan.Zero);

    /// <summary>The start of the period <paramref name="now"/> falls in.</summary>
    public static DateTimeOffset Period(DateTimeOffset now, TimeSpan every, TimeSpan offset)
    {
        var ticks = now.UtcTicks - offset.Ticks;
        return new DateTimeOffset(ticks - ticks % every.Ticks + offset.Ticks, TimeSpan.Zero);
    }

    /// <summary><paramref name="moment"/> without its fraction of a second.</summary>
    public static DateTimeOffset WholeSeconds(DateTimeOffset moment) =>
        new(moment.UtcTicks - moment.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>
    /// Cuts the range between <paramref name="horizon"/> and <paramref name="end"/> into slices, newest first.
    /// The last slice stops exactly at the horizon. Nothing is returned when the range is already covered.
    /// </summary>
    public static IEnumerable<JobWindow> Backfill(DateTimeOffset end, DateTimeOffset horizon, TimeSpan slice)
    {
        while (end > horizon)
        {
            var start = end - slice;
            if (start < horizon)
                start = horizon;

            yield return new JobWindow(start, end);
            end = start;
        }
    }
}
