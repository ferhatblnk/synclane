using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class MigrationTests : PostgresTest
{
    [PostgresFact]
    public async Task A_new_database_gets_every_step_once_and_records_it()
    {
        await DropSchemaAsync();

        var first = await Store.MigrateAsync(CancellationToken.None);
        var second = await Store.MigrateAsync(CancellationToken.None);

        Assert.Equal(Migrations.All.Count, first);
        Assert.Equal(0, second);
        Assert.Empty(await Store.MissingMigrationsAsync(CancellationToken.None));
    }

    [PostgresFact]
    public async Task A_table_created_before_migrations_existed_is_adopted_with_its_jobs()
    {
        await DropSchemaAsync();
        await ExecuteAsync($"CREATE SCHEMA {Schema}");
        await ExecuteAsync(Migrations.All[0].Sql(Schema));
        await EnqueueAsync(Noon, Planned("a", key: "1"));

        Assert.Equal(new[] { 1 }, await Store.MissingMigrationsAsync(CancellationToken.None));

        await Store.MigrateAsync(CancellationToken.None);

        Assert.Empty(await Store.MissingMigrationsAsync(CancellationToken.None));
        Assert.Equal("a", Assert.Single(await JobsAsync()).Owner);
    }

    [PostgresFact]
    public async Task The_script_brings_an_empty_database_to_the_latest_version_and_can_be_run_again()
    {
        await DropSchemaAsync();

        await ExecuteAsync(SyncLaneSchema.Script(Schema));
        await ExecuteAsync(SyncLaneSchema.Script(Schema));

        Assert.Empty(await Store.MissingMigrationsAsync(CancellationToken.None));
        Assert.Equal(0, await Store.MigrateAsync(CancellationToken.None));
        Assert.Equal(1, await EnqueueAsync(Noon, Planned("a", key: "1")));
    }

    [PostgresFact]
    public async Task With_CreateSchema_off_a_missing_migration_stops_the_host_with_a_message_that_says_how_to_fix_it()
    {
        await DropSchemaAsync();
        using var host = Host(createSchema: false);

        var error = await Assert.ThrowsAsync<SyncLaneException>(() => host.StartAsync());

        Assert.Equal(SyncErrorCodes.MigrationMissing, error.Error.Code);
        Assert.Contains("missing migration 1", error.Message);
        Assert.Contains($"SyncLaneSchema.Script(\"{Schema}\")", error.Message);
    }

    [PostgresFact]
    public async Task With_CreateSchema_off_the_host_starts_once_the_script_is_applied()
    {
        await DropSchemaAsync();
        await ExecuteAsync(SyncLaneSchema.Script(Schema));
        using var host = Host(createSchema: false);

        await host.StartAsync();
        await host.StopAsync();
    }

    [PostgresFact]
    public async Task With_CreateSchema_on_the_host_applies_what_is_missing_at_start_up()
    {
        await DropSchemaAsync();
        using var host = Host(createSchema: true);

        await host.StartAsync();
        await host.StopAsync();

        Assert.Empty(await Store.MissingMigrationsAsync(CancellationToken.None));
    }

    private Task DropSchemaAsync() => ExecuteAsync($"DROP SCHEMA {Schema} CASCADE");

    private IHost Host(bool createSchema)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.CreateSchema = createSchema;
            options.RunPlanner = false;
            options.RunWorkers = false;
        });

        return builder.Build();
    }
}
