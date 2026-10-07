using Microsoft.Extensions.DependencyInjection;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class OptionsTests
{
    private const string AnyConnection = "Host=localhost;Database=app";

    [Fact]
    public void Job_types_run_in_the_order_they_were_declared()
    {
        var queue = new SyncLaneOptions()
            .AddQueue("stores", options => options
                .Snapshot<ProductHandler>("products")
                .Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30))
                .Handle<ProductHandler>("resync"))
            .Queue("stores");

        Assert.Equal(["products", "orders", "resync"], queue.Types.Select(type => type.Name));
        Assert.Equal([0, 1, 2], queue.Types.Select(type => type.Priority));
    }

    [Fact]
    public void A_connection_string_is_required()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSyncLane(_ => { }));

        Assert.Contains("ConnectionString", failure.Message);
    }

    [Theory]
    [InlineData("Jobs")]
    [InlineData("my-schema")]
    [InlineData("9lives")]
    [InlineData("jobs; DROP TABLE users")]
    [InlineData("")]
    public void A_schema_name_that_is_not_a_plain_identifier_is_refused(string schema)
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSyncLane(options =>
        {
            options.ConnectionString = AnyConnection;
            options.Schema = schema;
        }));
    }

    [Fact]
    public void A_queue_or_a_job_type_cannot_be_declared_twice()
    {
        var options = new SyncLaneOptions().AddQueue("stores", queue => queue.Snapshot<ProductHandler>("products"));

        Assert.Throws<InvalidOperationException>(() => options.AddQueue("stores", _ => { }));
        Assert.Throws<InvalidOperationException>(() => options.Queue("stores").Snapshot<ProductHandler>("products"));
    }

    [Fact]
    public void Settings_that_cannot_work_are_refused_at_start_up()
    {
        Assert.Throws<InvalidOperationException>(() => Configure(queue => queue.Concurrency = 0));
        Assert.Throws<InvalidOperationException>(() => Configure(queue => queue.MaxAttempts = 0));
        Assert.Throws<InvalidOperationException>(() => Configure(queue => queue.RetryMaxDelay = TimeSpan.FromSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => Configure(queue => queue.Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.Zero)));
        Assert.Throws<InvalidOperationException>(() => Configure(queue => queue.Snapshot<ProductHandler>("products", TimeSpan.Zero)));
        Assert.Throws<InvalidOperationException>(() => Configure(queue => queue.Snapshot<ProductHandler>("products", TimeSpan.FromHours(1), TimeSpan.FromHours(2))));
    }

    [Fact]
    public void An_unknown_queue_is_an_error_not_an_empty_result()
    {
        Assert.Throws<ArgumentException>(() => new SyncLaneOptions().Queue("missing"));
    }

    [Fact]
    public void Handlers_and_owner_sources_are_registered_as_scoped_services()
    {
        var services = new ServiceCollection().AddSyncLane(options =>
        {
            options.ConnectionString = AnyConnection;
            options.AddQueue("stores", queue => queue.Snapshot<ProductHandler>("products"));
        });

        var handler = Assert.Single(services, service => service.ServiceType == typeof(ProductHandler));
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);
    }

    [Fact]
    public void The_schema_file_in_the_docs_is_the_one_the_library_creates()
    {
        var documented = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema.sql"));

        Assert.Equal(Normalize(JobSql.Ddl("synclane")), Normalize(documented));
    }

    private static void Configure(Action<QueueOptions> configure) =>
        new ServiceCollection().AddSyncLane(options =>
        {
            options.ConnectionString = AnyConnection;
            options.AddQueue("stores", configure);
        });

    private static string Normalize(string text) => text.ReplaceLineEndings("\n").Trim();
}
