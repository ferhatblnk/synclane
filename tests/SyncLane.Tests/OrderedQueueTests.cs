using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Ordered queues: a lane's jobs in the order they were created, held behind one that failed until it is fixed or skipped.</summary>
public class OrderedQueueTests : PostgresTest
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task A_lane_s_next_job_waits_while_an_older_one_failed_and_other_lanes_go_on()
    {
        await EnqueueAsync(Noon, Planned("a", key: "movement-1"), Planned("a", key: "movement-2"), Planned("b", key: "movement-3"));
        var first = (await LeaseOrderedAsync("w1"))!;
        await Store.SettleAsync(first.Id, "w1", JobStatus.Failed, "inventory service down", Noon.AddHours(1), Noon, CancellationToken.None);

        var otherLane = await LeaseOrderedAsync("w2");
        var nothing = await LeaseOrderedAsync("w3");

        Assert.Equal("movement-1", first.DedupKey);
        Assert.Equal("movement-3", otherLane!.DedupKey);
        Assert.Null(nothing);
    }

    [PostgresFact]
    public async Task An_abandoned_job_holds_its_lane_until_it_is_requeued_and_completes()
    {
        await EnqueueAsync(Noon, Planned("a", key: "movement-1"), Planned("a", key: "movement-2"));
        var first = (await LeaseOrderedAsync("w"))!;
        await Store.SettleAsync(first.Id, "w", JobStatus.Abandoned, "no matching product", Noon, Noon, CancellationToken.None);

        var held = await LeaseOrderedAsync("w");
        (await Monitor().RequeueJobAsync(first.Id, by: "ops")).ThrowIfFailed();
        var again = (await LeaseOrderedAsync("w"))!;
        await Store.CompleteAsync(again.Id, "w", Noon, CancellationToken.None);
        var second = await LeaseOrderedAsync("w");

        Assert.Null(held);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal("movement-2", second!.DedupKey);
    }

    [PostgresFact]
    public async Task A_skipped_job_lets_its_lane_go_on_and_the_actions_log_says_who_skipped_which_job_and_why()
    {
        await EnqueueAsync(Noon, Planned("a", key: "movement-1"), Planned("a", key: "movement-2"));
        var first = (await LeaseOrderedAsync("w"))!;
        await Store.SettleAsync(first.Id, "w", JobStatus.Abandoned, "plan cancelled", Noon, Noon, CancellationToken.None);
        var monitor = Monitor();

        var skipped = await monitor.SkipJobAsync(first.Id, "plan was cancelled", by: "ops-1");
        var second = await LeaseOrderedAsync("w");
        var action = (await monitor.GetActionsAsync(new ActionQuery { Queue = Queue })).Items.Single();

        Assert.Equal(1, skipped.GetValueOrThrow());
        Assert.Equal("movement-2", second!.DedupKey);
        Assert.DoesNotContain(await JobsAsync(), job => job.Id == first.Id);
        Assert.Equal(("skip job", "ops-1", "a", "plan was cancelled"), (action.Action, action.By, action.Owner, action.Reason));
        Assert.Contains("movement-1", action.Target);
    }

    [PostgresFact]
    public async Task A_running_job_is_not_skipped_and_an_unknown_one_is_not_found()
    {
        await EnqueueAsync(Noon, Planned("a", key: "movement-1"));
        var running = (await LeaseOrderedAsync("w"))!;

        var refused = await Monitor().SkipJobAsync(running.Id, "too late");
        var missing = await Monitor().SkipJobAsync(999_999, "never was");

        Assert.Equal(SyncErrorCodes.JobStatusNotAllowed, refused.Error!.Code);
        Assert.Equal(SyncErrorCodes.JobNotFound, missing.Error!.Code);
        Assert.Single(await JobsAsync());
    }

    [PostgresFact]
    public async Task A_deferred_job_at_the_head_of_an_ordered_lane_runs_again_instead_of_waiting_for_the_jobs_behind_it()
    {
        await EnqueueAsync(Noon, Planned("a", key: "movement-1"), Planned("a", key: "movement-2"));
        var first = (await LeaseOrderedAsync("w"))!;
        await Store.SettleAsync(first.Id, "w", JobStatus.Deferred, "slow", Noon, Noon, CancellationToken.None);

        var ordered = await LeaseOrderedAsync("w");

        Assert.Equal(first.Id, ordered!.Id);
    }

    [PostgresFact]
    public async Task An_unordered_queue_lets_the_next_job_of_a_lane_pass_one_that_failed()
    {
        await EnqueueAsync(Noon, Planned("a", key: "movement-1"), Planned("a", key: "movement-2"));
        var first = (await LeaseAsync("w", Noon))!;
        await Store.SettleAsync(first.Id, "w", JobStatus.Failed, "down", Noon.AddHours(1), Noon, CancellationToken.None);

        var next = await LeaseAsync("w", Noon);

        Assert.Equal("movement-2", next!.DedupKey);
    }

    [PostgresFact]
    public async Task Who_asked_for_a_job_is_kept_with_it_and_a_sync_now_records_its_caller()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue.Handle<ProductHandler>("orders"));
        var client = new SyncLaneClient(options, Store, new Planning.QueuePlanner(Store, _clock), _clock);
        await ExecuteAsync($"INSERT INTO {Schema}.coverage (queue, owner, type, has_initial) VALUES ('{Queue}', 'b', 'orders', true)");

        var asked = await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders", RequestedBy = "user-42" });
        var tooLong = await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders", RequestedBy = new string('x', 201) });
        var synced = await Monitor().SyncNowAsync(Queue, "b", "orders", by: "order-webhook");

        Assert.Equal("user-42", (await JobAsync(asked.GetValueOrThrow())).RequestedBy);
        Assert.Equal(SyncErrorCodes.RequestedByInvalid, tooLong.Error!.Code);
        Assert.Equal("order-webhook", (await JobAsync(synced.GetValueOrThrow())).RequestedBy);
    }

    [PostgresFact]
    public async Task A_host_running_an_ordered_queue_holds_a_lane_behind_its_abandoned_job_until_it_is_skipped()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.ListenConnectionString = ListenConnectionString;
            options.Schema = Schema;
            options.RunPlanner = false;
            options.PollInterval = TimeSpan.FromMilliseconds(100);
            options.AddQueue(Queue, queue =>
            {
                queue.Ordered = true;
                queue.Concurrency = 2;
                queue.Handle<MovementHandler>("movements");
            });
        });
        using var host = builder.Build();
        await host.StartAsync();
        var client = host.Services.GetRequiredService<ISyncLane>();

        var first = (await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "movements", DedupKey = MovementHandler.Refused })).GetValueOrThrow();
        await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "movements", DedupKey = "movement-2" });
        await WaitAsync(jobs => jobs.Any(job => job.Id == first && job.Status == JobStatus.Abandoned));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var held = (await JobsAsync()).Single(job => job.DedupKey == "movement-2").Status;

        (await host.Services.GetRequiredService<ISyncLaneMonitor>().SkipJobAsync(first, "fixed by hand")).ThrowIfFailed();
        await WaitAsync(jobs => jobs.Any(job => job.DedupKey == "movement-2" && job.Status == JobStatus.Completed));

        await host.StopAsync();
        Assert.Equal(JobStatus.Pending, held);
    }

    private Task<Job?> LeaseOrderedAsync(string worker) =>
        Store.LeaseAsync(Queue, worker, Noon, Noon.AddMinutes(5), CancellationToken.None, ordered: true);

    private Monitoring.SyncLaneMonitor Monitor() => MonitorTests.MonitorFor(Store, Schema, _clock);

    private async Task WaitAsync(Func<IReadOnlyList<Job>, bool> done)
    {
        using var timeout = new CancellationTokenSource(Patience);
        // Sequential: polling the table until the run has reached what the test waits for.
        while (!done(await JobsAsync()))
            await Task.Delay(50, timeout.Token);
    }

    /// <summary>Gives up the movement the test marks as refused, and completes every other.</summary>
    private sealed class MovementHandler : IJobHandler
    {
        public const string Refused = "movement-refused";

        public Task<JobPage> RunPageAsync(Job job, CancellationToken cancellationToken) =>
            job.DedupKey == Refused
                ? throw new JobAbandonedException("No matching product for the movement.")
                : Task.FromResult(new JobPage(null, 1));
    }
}
