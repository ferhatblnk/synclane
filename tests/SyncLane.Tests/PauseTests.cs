using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Monitoring;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>The emergency stop: nothing of a paused queue or owner is taken, and what runs is cut and handed back.</summary>
public class PauseTests : PostgresTest
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task Nothing_of_a_paused_queue_or_owner_is_taken_until_the_pause_is_lifted_or_ends()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"), Planned("b", key: "2"));
        var monitor = Monitor();

        await monitor.PauseAsync(Queue, null, "maintenance", by: "ops");
        var whilePaused = await LeaseAsync("w", Noon);
        await monitor.ResumeAsync(Queue, by: "ops");
        await monitor.PauseAsync(Queue, "a", "owner a is moving", by: "ops", until: Noon.AddMinutes(30));
        var ownerPaused = await LeaseAsync("w", Noon);
        await Store.CompleteAsync(ownerPaused!.Id, "w", Noon, CancellationToken.None);
        var stillPaused = await LeaseAsync("w2", Noon.AddMinutes(10));
        var afterTheEnd = await LeaseAsync("w3", Noon.AddMinutes(31));

        Assert.Null(whilePaused);
        Assert.Equal("b", ownerPaused.Owner);
        Assert.Null(stillPaused);
        Assert.Equal("a", afterTheEnd!.Owner);
    }

    [PostgresFact]
    public async Task A_running_job_is_cut_at_once_and_handed_back_without_an_attempt_then_finishes_after_the_resume()
    {
        using var host = Host();
        await host.StartAsync();
        await host.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(CancellationToken.None);
        var monitor = host.Services.GetRequiredService<ISyncLaneMonitor>();
        var gate = host.Services.GetRequiredService<SlowPages>();

        await host.Services.GetRequiredService<ISyncLane>().EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await gate.SecondPageStarted.Task.WaitAsync(Patience);

        await monitor.PauseAsync(Queue, null, "emergency", by: "ops");
        var handedBack = await WaitForAsync(job => job.Status == JobStatus.Pending && job.LeasedBy is null);
        await monitor.ResumeAsync(Queue, by: "ops");
        gate.Release();
        var completed = await WaitForAsync(job => job.Status == JobStatus.Completed);
        await host.StopAsync();

        Assert.Equal(("page-2", 0), (handedBack.Cursor, handedBack.AttemptCount));
        Assert.Equal(3, completed.ProcessedCount);
    }

    [PostgresFact]
    public async Task A_worker_that_missed_the_announcement_stops_at_its_next_page()
    {
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var job = (await LeaseAsync("w", Noon))!;
        await ExecuteAsync($"INSERT INTO {Schema}.pauses (queue, owner, paused_by, reason, paused_at) VALUES ('{Queue}', '', 'ops', 'quiet', now())");

        var hold = await Store.CheckpointAsync(job.Id, "w", "page-2", 10, Noon, Noon.AddMinutes(5), CancellationToken.None);
        var beat = await Store.HeartbeatAsync(job.Id, "w", Noon, Noon.AddMinutes(5), CancellationToken.None);

        Assert.Equal((Storage.Hold.Paused, Storage.Hold.Paused), (hold, beat));
    }

    [PostgresFact]
    public async Task A_running_job_whose_pause_was_not_announced_finishes_its_page_and_is_handed_back_there()
    {
        using var host = Host();
        await host.StartAsync();
        var gate = host.Services.GetRequiredService<SlowPages>();

        await host.Services.GetRequiredService<ISyncLane>().EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await gate.SecondPageStarted.Task.WaitAsync(Patience);

        // Written straight to the table, as an instance that never heard the announcement would find it.
        await ExecuteAsync($"INSERT INTO {Schema}.pauses (queue, owner, paused_by, reason, paused_at) VALUES ('{Queue}', '', 'ops', 'quiet', now())");
        gate.Release();
        var handedBack = await WaitForAsync(job => job.Status == JobStatus.Pending && job.LeasedBy is null);
        await host.StopAsync();

        Assert.Equal(("page-3", 0, 2L), (handedBack.Cursor, handedBack.AttemptCount, handedBack.ProcessedCount));
    }

    [PostgresFact]
    public async Task A_page_that_hangs_is_cut_by_the_heartbeat_when_its_pause_was_not_announced()
    {
        using var host = Host(leaseDuration: TimeSpan.FromSeconds(1));
        await host.StartAsync();
        var gate = host.Services.GetRequiredService<SlowPages>();

        await host.Services.GetRequiredService<ISyncLane>().EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await gate.SecondPageStarted.Task.WaitAsync(Patience);
        await ExecuteAsync($"INSERT INTO {Schema}.pauses (queue, owner, paused_by, reason, paused_at) VALUES ('{Queue}', '', 'ops', 'quiet', now())");
        var handedBack = await WaitForAsync(job => job.Status == JobStatus.Pending && job.LeasedBy is null);
        await host.StopAsync();

        Assert.Equal(("page-2", 0), (handedBack.Cursor, handedBack.AttemptCount));
    }

    [PostgresFact]
    public async Task A_resume_wakes_idle_workers_at_once_instead_of_at_their_next_poll()
    {
        using var host = Host(pollInterval: TimeSpan.FromMinutes(5));
        await host.StartAsync();
        await host.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(CancellationToken.None);
        var monitor = host.Services.GetRequiredService<ISyncLaneMonitor>();
        var gate = host.Services.GetRequiredService<SlowPages>();
        gate.Release();

        await monitor.PauseAsync(Queue, null, "maintenance", by: "ops");
        await host.Services.GetRequiredService<ISyncLane>().EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await Task.Delay(300);
        var whilePaused = (await JobsAsync()).Single().Status;
        await monitor.ResumeAsync(Queue, by: "ops");
        var completed = await WaitForAsync(job => job.Status == JobStatus.Completed);
        await host.StopAsync();

        Assert.Equal(JobStatus.Pending, whilePaused);
        Assert.Equal(3, completed.ProcessedCount);
    }

    [PostgresFact]
    public async Task Every_action_is_logged_with_who_did_it_and_why_newest_first()
    {
        var abandoned = await InsertRowAsync("a", JobMode.Recurring, JobStatus.Abandoned, Noon, key: "1", completedAt: Noon);
        await ExecuteAsync($"UPDATE {Schema}.jobs SET error = 'SL5106: refused' WHERE id = {abandoned}");
        var monitor = Monitor();

        await monitor.PauseAsync(Queue, "a", "token rotation", by: "ayse");
        await monitor.ResumeAsync(Queue, "a", by: "ayse");
        await monitor.RequeueByCodeAsync(Queue, "SL5106", by: "mehmet");
        await monitor.RequeueJobAsync(999_999, by: "nobody");
        var page = await monitor.GetActionsAsync(new ActionQuery { Queue = Queue, Limit = 2 });
        var next = await monitor.GetActionsAsync(new ActionQuery { Queue = Queue, Limit = 2, Cursor = page.NextCursor });

        Assert.Equal([("requeue by code", "mehmet", 1), ("resume", "ayse", 1)], page.Items.Select(action => (action.Action, action.By, action.Changed)));
        var pause = Assert.Single(next.Items);
        Assert.Equal(("pause", "a", "token rotation"), (pause.Action, pause.Owner, pause.Reason));
    }

    [PostgresFact]
    public async Task The_overview_freshness_and_pause_list_show_what_is_paused_and_an_end_in_the_past_is_refused()
    {
        await ExecuteAsync($"INSERT INTO {Schema}.coverage (queue, owner, type, has_initial) VALUES ('{Queue}', 'a', 'orders', true)");
        await EnqueueAsync(Noon, Planned("a", key: "1"));
        var monitor = Monitor();

        await monitor.PauseAsync(Queue, null, "maintenance", by: "ops", until: Noon.AddHours(1));
        var overview = Assert.Single((await monitor.GetOverviewAsync()).GetValueOrThrow());
        var freshness = Assert.Single((await monitor.GetFreshnessAsync(new FreshnessQuery { Queue = Queue })).Items);
        var pauses = (await monitor.GetPausesAsync()).GetValueOrThrow();
        var past = await monitor.PauseAsync(Queue, null, "too late", until: Noon.AddMinutes(-1));

        Assert.Equal(("ops", "maintenance", Noon.AddHours(1)), (overview.Pause!.By, overview.Pause.Reason, overview.Pause.Until!.Value));
        Assert.True(freshness.Paused);
        Assert.Equal((Queue, (string?)null), (Assert.Single(pauses).Queue, pauses[0].Owner));
        Assert.Equal(SyncErrorCodes.PauseEndInvalid, past.Error!.Code);
    }

    private SyncLaneMonitor Monitor() => MonitorTests.MonitorFor(Store, Schema, _clock);

    private async Task<Job> WaitForAsync(Func<Job, bool> done)
    {
        using var timeout = new CancellationTokenSource(Patience);
        // Sequential: polling the table until the job reaches the state the test waits for.
        while (true)
        {
            if ((await JobsAsync()).SingleOrDefault() is { } job && done(job))
                return job;

            await Task.Delay(50, timeout.Token);
        }
    }

    private IHost Host(TimeSpan? pollInterval = null, TimeSpan? leaseDuration = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<SlowPages>();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.ListenConnectionString = ListenConnectionString;
            options.Schema = Schema;
            options.RunPlanner = false;
            options.PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
            options.AddQueue(Queue, queue =>
            {
                queue.LeaseDuration = leaseDuration ?? queue.LeaseDuration;
                queue.Handle<SlowPagesHandler>("orders");
            });
        });
        return builder.Build();
    }

    /// <summary>Three pages; the second waits until the test lets it go, or until it is cut.</summary>
    private sealed class SlowPages
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondPageStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => _released.Task;

        public void Release() => _released.TrySetResult();
    }

    private sealed class SlowPagesHandler(SlowPages gate) : IJobHandler
    {
        public async Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken)
        {
            switch (job.Cursor)
            {
                case null:
                    return new JobPage("page-2", 1);
                case "page-2":
                    gate.SecondPageStarted.TrySetResult();
                    await gate.Released.WaitAsync(cancellationToken);
                    return new JobPage("page-3", 1);
                default:
                    return new JobPage(null, 1);
            }
        }
    }
}
