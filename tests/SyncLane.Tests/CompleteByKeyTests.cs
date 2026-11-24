using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Completing jobs by their keys: work that was done some other way is closed, wherever its job stands.</summary>
public class CompleteByKeyTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresTheory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Deferred)]
    [InlineData(JobStatus.Abandoned)]
    public async Task A_job_that_stopped_is_completed_by_its_key_and_its_error_is_cleared(JobStatus stopped)
    {
        var id = (await Client().EnqueueAsync(NewJob("a", "movement-1"))).GetValueOrThrow();
        await Store.SettleAsync((await LeaseAsync("w", Noon))!, stopped, "the other side is down", Noon.AddHours(1), Noon, CancellationToken.None);

        var completed = (await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1"])).GetValueOrThrow();
        var job = (await Client().GetJobAsync(id)).GetValueOrThrow();

        Assert.Equal(1, completed);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(Noon, job.CompletedAt);
        Assert.Null(job.Error);
    }

    [PostgresFact]
    public async Task Several_keys_are_completed_in_one_call_and_a_key_given_twice_counts_once()
    {
        await Client().EnqueueAsync(NewJob("a", "movement-1"));
        await Client().EnqueueAsync(NewJob("a", "movement-2"));
        await Client().EnqueueAsync(NewJob("a", "movement-3"));

        var completed = (await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1", "movement-2", "movement-1"])).GetValueOrThrow();

        Assert.Equal(2, completed);
        Assert.Equal("movement-3", (await LeaseAsync("w", Noon))!.DedupKey);
    }

    [PostgresFact]
    public async Task A_running_job_is_completed_and_its_worker_s_late_result_does_not_reopen_it()
    {
        var id = (await Client().EnqueueAsync(NewJob("a", "movement-1"))).GetValueOrThrow();
        var running = (await LeaseAsync("w", Noon))!;

        var completed = (await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1"])).GetValueOrThrow();
        var settledLate = await Store.SettleAsync(running, JobStatus.Failed, "timed out", Noon.AddHours(1), Noon, CancellationToken.None);
        var job = (await Client().GetJobAsync(id)).GetValueOrThrow();

        Assert.Equal(1, completed);
        Assert.False(settledLate);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.LeasedBy);
    }

    [PostgresFact]
    public async Task Keys_that_match_no_job_complete_nothing_and_are_not_an_error()
    {
        var result = await Client().CompleteAsync(Queue, "a", "delivery", ["movement-404"]);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value);
    }

    [PostgresFact]
    public async Task An_empty_list_of_keys_completes_nothing()
    {
        await Client().EnqueueAsync(NewJob("a", "movement-1"));

        Assert.Equal(0, (await Client().CompleteAsync(Queue, "a", "delivery", [])).GetValueOrThrow());
        Assert.Single(await JobsAsync(JobStatus.Pending));
    }

    [PostgresFact]
    public async Task A_job_that_already_completed_keeps_the_moment_it_completed()
    {
        var id = (await Client().EnqueueAsync(NewJob("a", "movement-1"))).GetValueOrThrow();
        await Store.CompleteAsync((await LeaseAsync("w", Noon))!, Noon, CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(2));

        var completed = (await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1"])).GetValueOrThrow();

        Assert.Equal(0, completed);
        Assert.Equal(Noon, (await Client().GetJobAsync(id)).GetValueOrThrow().CompletedAt);
    }

    [PostgresFact]
    public async Task Another_owner_s_job_and_a_planned_job_with_the_same_key_are_left_alone()
    {
        await Client().EnqueueAsync(NewJob("b", "movement-1"));
        await InsertRowAsync("a", JobMode.Recurring, JobStatus.Pending, Noon, key: "movement-1", type: "delivery");

        var completed = (await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1"])).GetValueOrThrow();

        Assert.Equal(0, completed);
        Assert.Equal(2, (await JobsAsync(JobStatus.Pending)).Count);
    }

    [PostgresFact]
    public async Task A_job_of_another_type_with_the_same_key_is_left_alone()
    {
        await Client().EnqueueAsync(NewJob("a", "movement-1") with { Type = "pickup" });

        var completed = (await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1"])).GetValueOrThrow();

        Assert.Equal(0, completed);
        Assert.Single(await JobsAsync(JobStatus.Pending));
    }

    [PostgresFact]
    public async Task An_ordered_lane_goes_on_once_the_job_holding_it_is_completed_by_its_key()
    {
        await Client().EnqueueAsync(NewJob("a", "movement-1"));
        await Client().EnqueueAsync(NewJob("a", "movement-2"));
        await Store.SettleAsync((await LeaseOrderedAsync("w"))!, JobStatus.Abandoned, "rejected", Noon, Noon, CancellationToken.None);
        var held = await LeaseOrderedAsync("w");

        await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1"]);
        var next = await LeaseOrderedAsync("w");

        Assert.Null(held);
        Assert.Equal("movement-2", next!.DedupKey);
    }

    [PostgresFact]
    public async Task A_queue_or_a_job_type_that_is_not_declared_is_refused()
    {
        var unknownQueue = await Client().CompleteAsync("nowhere", "a", "delivery", ["movement-1"]);
        var unknownType = await Client().CompleteAsync(Queue, "a", "nothing", ["movement-1"]);

        Assert.Equal(SyncErrorCodes.QueueNotDeclared, unknownQueue.Error!.Code);
        Assert.Equal(SyncErrorCodes.JobTypeNotDeclared, unknownType.Error!.Code);
    }

    [PostgresFact]
    public async Task Completing_by_key_counts_no_completed_job_and_no_records_in_the_hourly_volume()
    {
        await Client().EnqueueAsync(NewJob("a", "movement-1"));

        await Client().CompleteAsync(Queue, "a", "delivery", ["movement-1"]);

        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.job_stats"));
    }

    private static NewJob NewJob(string owner, string key) =>
        new() { Queue = Queue, Owner = new JobOwner(owner), Type = "delivery", DedupKey = key };

    private Task<Job?> LeaseOrderedAsync(string worker) =>
        Store.LeaseAsync(Queue, worker, Noon, Noon.AddMinutes(5), CancellationToken.None, ordered: true);

    private SyncLaneClient Client()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue.Handle<ScriptedHandler>("delivery").Handle<ScriptedHandler>("pickup"));
        return new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock);
    }
}
