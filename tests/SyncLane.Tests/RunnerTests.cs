using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SyncLane.Running;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class RunnerTests : PostgresTest
{
    private const string Worker = "w";

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_job_runs_page_by_page_until_there_is_no_next_cursor()
    {
        var handler = new ScriptedHandler((job, _) => Task.FromResult(job.Cursor switch
        {
            null => new JobPage("2", 100),
            "2" => new JobPage("3", 100),
            _ => new JobPage(null, 40)
        }));

        var job = await RunAsync(handler);

        Assert.Equal([null, "2", "3"], handler.Cursors);
        Assert.Equal((JobStatus.Completed, 240L, 1), (job.Status, job.ProcessedCount, job.AttemptCount));
        Assert.Null(job.Cursor);
        Assert.Null(job.Error);
    }

    [PostgresFact]
    public async Task A_failed_job_runs_again_from_the_page_it_stopped_at()
    {
        var failures = 1;
        var handler = new ScriptedHandler((job, _) => job.Cursor switch
        {
            null => Task.FromResult(new JobPage("2", 100)),
            "2" when failures-- > 0 => throw new TimeoutException("No answer in 30 s."),
            "2" => Task.FromResult(new JobPage("3", 100)),
            _ => Task.FromResult(new JobPage(null, 40))
        });

        var failed = await RunAsync(handler);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var completed = await RunAsync(handler);

        Assert.Equal((JobStatus.Failed, "2", 100L, 1), (failed.Status, failed.Cursor, failed.ProcessedCount, failed.AttemptCount));
        Assert.Equal(("TimeoutException: No answer in 30 s.", Noon.AddMinutes(1)), (failed.Error, failed.NextRunAt));
        Assert.Equal([null, "2", "2", "3"], handler.Cursors);
        Assert.Equal((JobStatus.Completed, 240L, 2), (completed.Status, completed.ProcessedCount, completed.AttemptCount));
        Assert.Null(completed.Error);
    }

    [PostgresFact]
    public async Task A_job_that_keeps_failing_is_deferred_and_then_abandoned()
    {
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("Always broken."));
        var queue = Options<ScriptedHandler>(options => options.MaxAttempts = 2);

        var first = await RunAsync(handler, queue);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await RunAsync(handler, queue);
        var third = await RunAsync(handler, queue);
        var afterwards = await LeaseAsync(Worker, _clock.GetUtcNow().AddDays(1));

        Assert.Equal((JobStatus.Failed, 1), (first.Status, first.AttemptCount));
        Assert.Equal((JobStatus.Deferred, 2), (second.Status, second.AttemptCount));
        Assert.Equal((JobStatus.Abandoned, 3), (third.Status, third.AttemptCount));
        Assert.Equal(_clock.GetUtcNow(), third.CompletedAt);
        Assert.Null(afterwards);
    }

    [PostgresFact]
    public async Task A_throttled_job_waits_as_long_as_the_other_side_asked()
    {
        var handler = new ScriptedHandler((_, _) => throw new JobThrottledException("429 Too Many Requests", TimeSpan.FromSeconds(90)));

        var job = await RunAsync(handler);

        Assert.Equal((JobStatus.Failed, Noon.AddSeconds(90)), (job.Status, job.NextRunAt));
    }

    [PostgresFact]
    public async Task A_handler_can_give_a_job_up_on_the_first_attempt()
    {
        var handler = new ScriptedHandler((_, _) => throw new JobAbandonedException("Access was revoked."));

        var job = await RunAsync(handler);

        Assert.Equal((JobStatus.Abandoned, 1, "JobAbandonedException: Access was revoked."), (job.Status, job.AttemptCount, job.Error));
    }

    [PostgresFact]
    public async Task A_stopping_worker_hands_its_job_back_with_the_cursor_and_the_attempt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        var queue = Options<BlockingHandler>();
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var leased = await LeaseAsync(Worker, Noon);

        var running = Runner(new BlockingHandler(started)).RunAsync(queue, leased!, Worker, stopping.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.CancelAsync();
        await running;

        var job = await JobAsync(leased!.Id);
        Assert.Equal((JobStatus.Pending, BlockingHandler.SecondPage, 10L, 0), (job.Status, job.Cursor, job.ProcessedCount, job.AttemptCount));
        Assert.Null(job.LeasedBy);
    }

    [PostgresFact]
    public async Task A_job_without_a_handler_is_abandoned_with_a_reason()
    {
        await EnqueueAsync(Noon, Planned("a", "forgotten", key: "1"));
        var leased = await LeaseAsync(Worker, Noon);

        await Runner(new ScriptedHandler((_, _) => throw new InvalidOperationException("Must not run."))).RunAsync(Options<ScriptedHandler>(), leased!, Worker, CancellationToken.None);

        var job = await JobAsync(leased!.Id);
        Assert.Equal(JobStatus.Abandoned, job.Status);
        Assert.Contains("No handler is declared for job type 'forgotten'", job.Error);
    }

    [PostgresFact]
    public async Task A_job_that_keeps_taking_its_worker_down_is_abandoned_instead_of_running_forever()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new JobPage(null, 1)));
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        await ExecuteAsync($"UPDATE {Schema}.jobs SET attempt_count = 6");

        var job = await RunAsync(handler, enqueue: false);

        Assert.Empty(handler.Cursors);
        Assert.Equal((JobStatus.Abandoned, 7), (job.Status, job.AttemptCount));
    }

    [PostgresFact]
    public async Task A_worker_that_lost_its_job_stops_without_touching_it()
    {
        var handler = new ScriptedHandler(async (_, _) =>
        {
            await ExecuteAsync($"UPDATE {Schema}.jobs SET leased_by = 'someone-else'");
            return new JobPage("2", 100);
        });

        var job = await RunAsync(handler);

        Assert.Single(handler.Cursors);
        Assert.Equal((JobStatus.Running, "someone-else", 0L), (job.Status, job.LeasedBy, job.ProcessedCount));
        Assert.Null(job.Cursor);
    }

    [PostgresFact]
    public async Task The_lease_is_renewed_while_a_long_page_runs()
    {
        var lease = TimeSpan.FromSeconds(2);
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            return new JobPage(null, 1);
        });
        var queue = Options<ScriptedHandler>(options => options.LeaseDuration = lease);
        var start = DateTimeOffset.UtcNow;
        await EnqueueAsync(start, Planned("a", key: "1"));
        var leased = await Store.LeaseAsync(Queue, Worker, start, start + lease, CancellationToken.None);

        var running = Runner(handler, TimeProvider.System).RunAsync(queue, leased!, Worker, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var takenOver = await LeaseAsync("other", DateTimeOffset.UtcNow, lease);
        await running;

        Assert.Null(takenOver);
        Assert.Equal(JobStatus.Completed, (await JobAsync(leased!.Id)).Status);
    }

    [PostgresFact]
    public async Task Every_page_gets_a_handler_from_a_scope_of_its_own()
    {
        var log = new ScopeLog();
        var services = new ServiceCollection().AddSingleton(log).AddScoped<ScopedHandler>().BuildServiceProvider();
        var runner = new JobRunner(Store, services.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<JobRunner>.Instance);
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var leased = await LeaseAsync(Worker, Noon);

        await runner.RunAsync(Options<ScopedHandler>(), leased!, Worker, CancellationToken.None);

        Assert.Equal(3, log.Instances.Distinct().Count());
        Assert.Equal(JobStatus.Completed, (await JobAsync(leased!.Id)).Status);
    }

    private async Task<Job> RunAsync(ScriptedHandler handler, QueueOptions? queue = null, bool enqueue = true)
    {
        if (enqueue)
            await EnqueueAsync(Noon, Planned("a", key: "1"));

        var leased = await LeaseAsync(Worker, _clock.GetUtcNow())
            ?? throw new InvalidOperationException("No job is due.");

        await Runner(handler).RunAsync(queue ?? Options<ScriptedHandler>(), leased, Worker, CancellationToken.None);
        return await JobAsync(leased.Id);
    }

    private JobRunner Runner<THandler>(THandler handler, TimeProvider? time = null) where THandler : class, IJobHandler
    {
        var services = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
        return new JobRunner(Store, services.GetRequiredService<IServiceScopeFactory>(), time ?? _clock, NullLogger<JobRunner>.Instance);
    }

    private static QueueOptions Options<THandler>(Action<QueueOptions>? tune = null) where THandler : class, IJobHandler =>
        new SyncLaneOptions()
            .AddQueue(Queue, options =>
            {
                options.Snapshot<THandler>("orders");
                tune?.Invoke(options);
            })
            .Queue(Queue);
}
