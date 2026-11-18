using SyncLane.Storage;

namespace SyncLane.Tests;

public class AnalyzeRuleTests
{
    [Theory]
    [InlineData(1_000, -1, true)]
    [InlineData(1_000, 0, true)]
    [InlineData(999, 0, false)]
    [InlineData(1_000, 10_000, false)]
    [InlineData(1_001, 10_000, true)]
    [InlineData(5_000, 1_000_000, false)]
    public void The_table_is_analysed_after_an_insert_of_a_thousand_jobs_that_is_more_than_a_tenth_of_it(int created, long believed, bool analysed) =>
        Assert.Equal(analysed, JobStore.StatisticsAreStale(created, believed));
}
