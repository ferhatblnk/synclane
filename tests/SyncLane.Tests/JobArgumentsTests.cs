using Microsoft.Extensions.DependencyInjection;
using SyncLane.Planning;
using SyncLane.Running;
using SyncLane.Telemetry;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Arguments a job is enqueued with: stored with the job, read by its handler, kept out of lists, logs and errors.</summary>
public class JobArgumentsTests : PostgresTest
{
    private const string Body = """{"movement":42,"lines":[1,2]}""";

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_handler_reads_the_arguments_its_job_was_enqueued_with()
    {
        (await Client().EnqueueAsync(NewJob("delivery-1", Body))).ThrowIfFailed();

        var leased = (await LeaseAsync("w", Noon))!;

        Assert.Equal(Body, leased.Arguments);
    }

    [PostgresFact]
    public async Task A_job_enqueued_without_arguments_has_none()
    {
        (await Client().EnqueueAsync(NewJob("delivery-1", arguments: null))).ThrowIfFailed();

        Assert.Null((await LeaseAsync("w", Noon))!.Arguments);
    }

    [PostgresFact]
    public async Task A_row_written_without_the_arguments_column_is_read_with_none()
    {
        await InsertRowAsync("a", JobMode.Manual, JobStatus.Pending, Noon, key: "delivery-1", type: "delivery");

        Assert.Null((await LeaseAsync("w", Noon))!.Arguments);
    }

    [PostgresTheory]
    [InlineData(262_145, false)]
    [InlineData(10, true)]
    public async Task Arguments_that_are_too_long_or_hold_a_null_character_are_refused_and_nothing_is_enqueued(int length, bool withNull)
    {
        var arguments = withNull ? "ab\0cd" : new string('x', length);

        var result = await Client().EnqueueAsync(NewJob("delivery-1", arguments));

        Assert.Equal(SyncErrorCodes.ArgumentsInvalid, result.Error!.Code);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.jobs"));
    }

    [PostgresFact]
    public async Task Arguments_of_exactly_the_limit_are_accepted()
    {
        var result = await Client().EnqueueAsync(NewJob("delivery-1", new string('x', 262_144)));

        Assert.True(result.Succeeded);
    }

    [PostgresFact]
    public async Task A_list_of_jobs_does_not_carry_arguments()
    {
        (await Client().EnqueueAsync(NewJob("delivery-1", Body))).ThrowIfFailed();

        var listed = (await Client().GetJobsAsync(new JobQuery { Queue = Queue })).Items.Single();

        Assert.Null(listed.Arguments);
    }

    [PostgresFact]
    public async Task Arguments_reach_neither_the_log_nor_the_stored_error_when_the_job_fails()
    {
        const string marker = "argument-marker-7f3a";
        (await Client().EnqueueAsync(NewJob("delivery-1", marker))).ThrowIfFailed();
        var log = new LogLines();
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("The other side is down."));
        var services = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
        var runner = new JobRunner(Store, new RunningJobs(), services.GetRequiredService<IServiceScopeFactory>(), _clock, new SyncLaneMetrics(), log.For<JobRunner>());
        var leased = (await LeaseAsync("w", Noon))!;

        await runner.RunAsync(Options().Queue(Queue), leased, "w", CancellationToken.None);
        var failed = await JobAsync(leased.Id);

        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("The other side is down.", failed.Error);
        Assert.DoesNotContain(marker, failed.Error);
        Assert.DoesNotContain(log.Lines, line => line.Text.Contains(marker, StringComparison.Ordinal));
    }

    private static NewJob NewJob(string key, string? arguments) =>
        new() { Queue = Queue, Owner = new JobOwner("a"), Type = "delivery", DedupKey = key, Arguments = arguments };

    private static SyncLaneOptions Options() =>
        new SyncLaneOptions().AddQueue(Queue, queue => queue.Handle<ScriptedHandler>("delivery"));

    private SyncLaneClient Client() => new(Options(), Store, new QueuePlanner(Store, _clock), _clock);
}
