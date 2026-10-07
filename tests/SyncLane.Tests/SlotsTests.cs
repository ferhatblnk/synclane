using SyncLane.Planning;

namespace SyncLane.Tests;

public class SlotsTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Backfill_cuts_the_range_into_slices_newest_first()
    {
        var windows = Slots.Backfill(Today, Today.AddDays(-60), TimeSpan.FromDays(30)).ToList();

        Assert.Equal(
            [
                new JobWindow(Today.AddDays(-30), Today),
                new JobWindow(Today.AddDays(-60), Today.AddDays(-30))
            ],
            windows);
    }

    [Fact]
    public void Backfill_stops_exactly_at_the_horizon()
    {
        var windows = Slots.Backfill(Today, Today.AddDays(-45), TimeSpan.FromDays(30)).ToList();

        Assert.Equal(2, windows.Count);
        Assert.Equal(new JobWindow(Today.AddDays(-45), Today.AddDays(-30)), windows[1]);
    }

    [Fact]
    public void Backfill_returns_nothing_when_the_range_is_already_covered()
    {
        Assert.Empty(Slots.Backfill(Today.AddDays(-60), Today.AddDays(-60), TimeSpan.FromDays(30)));
        Assert.Empty(Slots.Backfill(Today.AddDays(-90), Today.AddDays(-60), TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Day_is_midnight_utc_whatever_the_offset_of_the_clock()
    {
        var earlyMorningAtPlusThree = new DateTimeOffset(2026, 8, 11, 1, 30, 0, TimeSpan.FromHours(3));

        Assert.Equal(Today, Slots.Day(earlyMorningAtPlusThree));
    }

    [Fact]
    public void Period_is_the_same_for_every_moment_inside_it()
    {
        var every = TimeSpan.FromMinutes(15);

        var first = Slots.Period(Today.AddMinutes(30), every, TimeSpan.Zero);
        var last = Slots.Period(Today.AddMinutes(44).AddSeconds(59), every, TimeSpan.Zero);
        var next = Slots.Period(Today.AddMinutes(45), every, TimeSpan.Zero);

        Assert.Equal(Today.AddMinutes(30), first);
        Assert.Equal(first, last);
        Assert.Equal(Today.AddMinutes(45), next);
    }

    [Fact]
    public void Period_with_an_offset_starts_at_that_time_of_day()
    {
        var daily = TimeSpan.FromDays(1);
        var atTwo = TimeSpan.FromHours(2);

        Assert.Equal(Today.AddDays(-1).AddHours(2), Slots.Period(Today.AddHours(1), daily, atTwo));
        Assert.Equal(Today.AddHours(2), Slots.Period(Today.AddHours(2), daily, atTwo));
        Assert.Equal(Today.AddHours(2), Slots.Period(Today.AddHours(23), daily, atTwo));
    }
}
