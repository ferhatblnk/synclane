using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Numbers per environment from configuration, over what the code declares.</summary>
public class SettingsTests
{
    private const string AnyConnection = "Host=localhost;Database=app";

    [Fact]
    public void Every_setting_in_the_section_overrides_what_the_code_declared()
    {
        var options = Configure(new()
        {
            ["SyncLane:ConnectionString"] = "Host=db;Database=other",
            ["SyncLane:ListenConnectionString"] = "Host=db-direct;Database=other",
            ["SyncLane:ListenForChanges"] = "false",
            ["SyncLane:Schema"] = "jobs_test",
            ["SyncLane:CreateSchema"] = "false",
            ["SyncLane:RunPlanner"] = "false",
            ["SyncLane:RunWorkers"] = "false",
            ["SyncLane:PlanInterval"] = "00:02:00",
            ["SyncLane:PollInterval"] = "00:00:10",
            ["SyncLane:MaxBudgetWait"] = "00:01:00",
            ["SyncLane:Application"] = "worker",
            ["SyncLane:KeepCompletedJobs"] = "30.00:00:00",
            ["SyncLane:Queues:orders:Concurrency"] = "4",
            ["SyncLane:Queues:orders:MaxAttempts"] = "7",
            ["SyncLane:Queues:orders:LeaseDuration"] = "00:10:00",
            ["SyncLane:Queues:orders:RetryBaseDelay"] = "00:00:30",
            ["SyncLane:Queues:orders:RetryMaxDelay"] = "02:00:00",
            ["SyncLane:Queues:orders:ThrottleBaseDelay"] = "00:01:00",
            ["SyncLane:Queues:orders:ThrottleMaxDelay"] = "00:10:00",
            ["SyncLane:Queues:orders:Types:orders:Horizon"] = "730.00:00:00",
            ["SyncLane:Queues:orders:Types:orders:Slice"] = "7.00:00:00",
            ["SyncLane:Queues:orders:Types:orders:Every"] = "00:05:00",
            ["SyncLane:Queues:orders:Types:orders:Overlap"] = "00:02:00",
            ["SyncLane:Queues:orders:Types:orders:Offset"] = "00:01:00",
            ["SyncLane:Queues:orders:Types:products:Every"] = "06:00:00",
            ["SyncLane:Queues:orders:Types:products:Offset"] = "02:00:00",
            ["SyncLane:Budgets:api:Limit"] = "45",
            ["SyncLane:Budgets:api:Per"] = "00:00:30",
            ["SyncLane:Budgets:api:Burst"] = "5",
            ["SyncLane:Budgets:api:Start"] = "0.25"
        });

        var queue = options.Queue("orders");
        Assert.Equal(
            ("Host=db;Database=other", "Host=db-direct;Database=other", "jobs_test", false, false, false),
            (options.ConnectionString, options.ListenConnectionString, options.Schema, options.CreateSchema, options.RunPlanner, options.RunWorkers));
        Assert.False(options.ListenForChanges);
        Assert.Equal(
            (TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), "worker", (TimeSpan?)TimeSpan.FromDays(30)),
            (options.PlanInterval, options.PollInterval, options.MaxBudgetWait, options.Application, options.KeepCompletedJobs));
        Assert.Equal(
            (4, 7, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), TimeSpan.FromHours(2), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            (queue.Concurrency, queue.MaxAttempts, queue.LeaseDuration, queue.RetryBaseDelay, queue.RetryMaxDelay, queue.ThrottleBaseDelay, queue.ThrottleMaxDelay));
        Assert.Equal(
            new WindowedRule(TimeSpan.FromDays(730), TimeSpan.FromDays(7), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
            queue.Find("orders")!.Rule);
        Assert.Equal(new SnapshotRule(TimeSpan.FromHours(6), TimeSpan.FromHours(2)), queue.Find("products")!.Rule);
        Assert.Equal(new BudgetDescription("api", 45, TimeSpan.FromSeconds(30), 5, 0.25), Assert.Single(options.DescribeBudgets()));
    }

    [Fact]
    public void Without_a_setting_the_code_s_value_stays_and_types_keep_their_order()
    {
        var withSection = Configure(new() { ["SyncLane:Queues:orders:Types:products:Every"] = "06:00:00" });
        var codeOnly = Configure([]);

        var queue = withSection.Queue("orders");
        Assert.Equal(["orders", "products", "resync"], queue.Types.Select(type => type.Name));
        Assert.Equal(codeOnly.Queue("orders").Find("orders"), queue.Find("orders"));
        Assert.Equal((2, AnyConnection), (queue.Concurrency, withSection.ConnectionString));
        Assert.Equal(new SnapshotRule(TimeSpan.FromHours(6), TimeSpan.Zero), queue.Find("products")!.Rule);
    }

    [Fact]
    public void Names_ignore_case_as_configuration_keys_do_so_a_variable_in_capitals_works()
    {
        var options = Configure(new()
        {
            ["SYNCLANE:QUEUES:ORDERS:CONCURRENCY"] = "3",
            ["SYNCLANE:QUEUES:ORDERS:TYPES:PRODUCTS:EVERY"] = "06:00:00",
            ["synclane:budgets:API:limit"] = "9"
        });

        var queue = options.Queue("orders");
        Assert.Equal((3, 9), (queue.Concurrency, options.DescribeBudgets()[0].Limit));
        Assert.Equal(TimeSpan.FromHours(6), queue.Find("products")!.Rule!.Every);
    }

    [Fact]
    public void Names_that_differ_only_in_case_in_the_code_must_be_matched_exactly()
    {
        var options = new SyncLaneOptions { ConnectionString = AnyConnection };
        options.AddQueue("sync", queue => queue.Handle<ProductHandler>("a"));
        options.AddQueue("Sync", queue => queue.Handle<ProductHandler>("a"));

        SettingsFromConfiguration.Apply(options, Section(new() { ["SyncLane:Queues:Sync:Concurrency"] = "4" }));
        var failure = Assert.Throws<SyncLaneException>(() =>
            SettingsFromConfiguration.Apply(options, Section(new() { ["SyncLane:Queues:SYNC:Concurrency"] = "4" })));

        Assert.Equal((1, 4), (options.Queue("sync").Concurrency, options.Queue("Sync").Concurrency));
        Assert.Equal(SyncErrorCodes.SettingNotDeclared, failure.Errors.Single().Code);
    }

    [Fact]
    public void Every_unknown_setting_is_reported_at_once_with_its_path_and_what_can_be_set_there()
    {
        var failure = Assert.Throws<SyncLaneException>(() => Configure(new()
        {
            ["SyncLane:RunWorker"] = "false",
            ["SyncLane:Queues:orders:Concurency"] = "3",
            ["SyncLane:Queues:orders:Types:orders:Horizn"] = "30.00:00:00",
            ["SyncLane:Queues:orders:Types:products:Slice"] = "1.00:00:00",
            ["SyncLane:Queues:orders:Types:resync:Every"] = "01:00:00",
            ["SyncLane:Budgets:api:Rate"] = "3",
            ["SyncLane:Queues:invoices:Concurrency"] = "3"
        }));

        Assert.Equal(7, failure.Errors.Count);
        Assert.All(failure.Errors, error => Assert.Equal((SyncErrorCodes.SettingNotDeclared, SyncErrorKind.Configuration), (error.Code, error.Kind)));
        Assert.Contains(failure.Errors, error => error.Message.StartsWith("'SyncLane:Queues:orders:Concurency'", StringComparison.Ordinal) && error.Message.Contains("Concurrency"));
        Assert.Contains(failure.Errors, error => error.Message.StartsWith("'SyncLane:Queues:orders:Types:products:Slice'", StringComparison.Ordinal) && error.Message.EndsWith("Every, Offset.", StringComparison.Ordinal));
        Assert.Contains(failure.Errors, error => error.Message.StartsWith("'SyncLane:Queues:orders:Types:resync:Every'", StringComparison.Ordinal) && error.Message.EndsWith("nothing.", StringComparison.Ordinal));
        Assert.Contains(failure.Errors, error => error.Message.StartsWith("'SyncLane:Queues:invoices'", StringComparison.Ordinal) && error.Message.EndsWith("orders.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_that_cannot_be_read_names_its_path_and_never_the_value()
    {
        var secret = "value-" + Guid.NewGuid().ToString("N");
        var failure = Assert.Throws<SyncLaneException>(() => Configure(new()
        {
            ["SyncLane:Queues:orders:Concurrency"] = secret,
            ["SyncLane:Queues:orders:LeaseDuration"] = secret,
            ["SyncLane:RunWorkers"] = secret,
            ["SyncLane:Budgets:api:Start"] = secret,
            ["SyncLane:ConnectionString:Host"] = secret,
            ["SyncLane:Queues"] = secret
        }));

        Assert.Equal(6, failure.Errors.Count);
        Assert.All(failure.Errors, error => Assert.Equal(SyncErrorCodes.SettingValueInvalid, error.Code));
        Assert.DoesNotContain(secret, failure.Message);
        Assert.Contains(failure.Errors, error => error.Message.StartsWith("'SyncLane:Queues:orders:LeaseDuration' could not be read as a time span", StringComparison.Ordinal));
        Assert.Contains(failure.Errors, error => error.Message.StartsWith("'SyncLane:ConnectionString' could not be read as text", StringComparison.Ordinal));
    }

    [Fact]
    public void Values_from_configuration_are_checked_like_values_from_code()
    {
        var concurrency = Assert.Throws<SyncLaneException>(() => Configure(new() { ["SyncLane:Queues:orders:Concurrency"] = "0" }));
        var slice = Assert.Throws<SyncLaneException>(() => Configure(new() { ["SyncLane:Queues:orders:Types:orders:Slice"] = "00:00:00" }));
        var limit = Assert.Throws<SyncLaneException>(() => Configure(new() { ["SyncLane:Budgets:api:Limit"] = "-1" }));

        Assert.Equal(SyncErrorCodes.QueueSettingInvalid, concurrency.Errors.Single().Code);
        Assert.Equal(SyncErrorCodes.JobTypeSettingInvalid, slice.Errors.Single().Code);
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, limit.Errors.Single().Code);
    }

    [Fact]
    public void The_registered_options_are_the_overridden_ones()
    {
        var services = new ServiceCollection();
        services.AddSyncLane(Section(new() { ["SyncLane:Queues:orders:Concurrency"] = "5" }), Declare);

        var registered = services.BuildServiceProvider().GetRequiredService<SyncLaneOptions>();

        Assert.Equal(5, registered.Queue("orders").Concurrency);
    }

    private static SyncLaneOptions Configure(Dictionary<string, string?> values)
    {
        var options = new SyncLaneOptions();
        Declare(options);
        SettingsFromConfiguration.Apply(options, Section(values));
        options.Validate();
        return options;
    }

    private static IConfiguration Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("SyncLane");

    private static void Declare(SyncLaneOptions options)
    {
        options.ConnectionString = AnyConnection;
        options.AddBudget("api", 20, TimeSpan.FromMinutes(1));
        options.AddQueue("orders", queue =>
        {
            queue.Concurrency = 2;
            queue.Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30), TimeSpan.FromMinutes(15));
            queue.Snapshot<ProductHandler>("products");
            queue.Handle<ProductHandler>("resync");
        });
    }
}
