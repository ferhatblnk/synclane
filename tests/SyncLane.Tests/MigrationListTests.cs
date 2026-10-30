using SyncLane.Storage;

namespace SyncLane.Tests;

public class MigrationListTests
{
    [Fact]
    public void Versions_start_at_one_and_have_no_gaps()
    {
        Assert.Equal(Enumerable.Range(1, Migrations.All.Count), Migrations.All.Select(migration => migration.Version));
        Assert.Equal(Migrations.All.Count, SyncLaneSchema.LatestVersion);
    }

    [Fact]
    public void Names_can_be_placed_in_sql_as_they_are()
    {
        Assert.All(Migrations.All, migration => Assert.DoesNotContain('\'', migration.Name));
    }

    [Fact]
    public void The_script_refuses_a_schema_name_that_is_not_a_plain_identifier()
    {
        Assert.Throws<ArgumentException>(() => SyncLaneSchema.Script("Jobs; DROP TABLE x"));
    }

    [Fact]
    public void The_script_covers_every_step_for_the_schema_it_was_asked_for()
    {
        var script = SyncLaneSchema.Script("sync_jobs");

        Assert.All(Migrations.All, migration => Assert.Contains($"VALUES ({migration.Version}, '{migration.Name}')", script));
        Assert.DoesNotContain("synclane.", script);
    }
}
