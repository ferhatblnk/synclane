using SyncLane.Planning;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>Enqueueing a job again under its key: the job that is there takes the new request's place, wherever it stands.</summary>
public class EnqueueOrReplaceTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task The_first_call_creates_the_job_and_says_nothing_was_replaced()
    {
        var created = (await Client().EnqueueOrReplaceAsync(NewJob("product-7", "90"))).GetValueOrThrow();

        Assert.False(created.Replaced);
        Assert.Equal("90", (await LeaseAsync("w", Noon))!.Arguments);
    }

    [PostgresTheory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Deferred)]
    [InlineData(JobStatus.Abandoned)]
    public async Task A_job_that_stopped_is_replaced_in_place_and_runs_again_with_the_new_arguments(JobStatus stopped)
    {
        var first = (await Client().EnqueueOrReplaceAsync(NewJob("product-7", "90"))).GetValueOrThrow();
        var lease = (await LeaseAsync("w", Noon))!;
        await Store.SettleAsync(lease, stopped, "the other side is down", Noon.AddHours(1), Noon, CancellationToken.None);

        var second = (await Client().EnqueueOrReplaceAsync(NewJob("product-7", "70"))).GetValueOrThrow();
        var again = (await LeaseAsync("w", Noon))!;

        Assert.True(second.Replaced);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("70", again.Arguments);
        Assert.Equal(1, again.AttemptCount);
        Assert.Null(again.Error);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.jobs"));
    }

    [PostgresFact]
    public async Task A_waiting_job_takes_the_new_arguments_without_a_second_row()
    {
        await Client().EnqueueOrReplaceAsync(NewJob("product-7", "90"));

        var second = (await Client().EnqueueOrReplaceAsync(NewJob("product-7", "70"))).GetValueOrThrow();

        Assert.True(second.Replaced);
        Assert.Equal("70", (await LeaseAsync("w", Noon))!.Arguments);
        Assert.Null(await LeaseAsync("w2", Noon));
    }

    [PostgresFact]
    public async Task A_completed_job_is_replaced_and_runs_again_and_keeps_when_it_was_created()
    {
        var first = (await Client().EnqueueOrReplaceAsync(NewJob("product-7", "90"))).GetValueOrThrow();
        await Store.CompleteAsync((await LeaseAsync("w", Noon))!, Noon, CancellationToken.None);
        var createdAt = (await Client().GetJobAsync(first.Id)).GetValueOrThrow().CreatedAt;
        _clock.Advance(TimeSpan.FromHours(2));

        var second = (await Client().EnqueueOrReplaceAsync(NewJob("product-7", "70"))).GetValueOrThrow();
        var job = (await Client().GetJobAsync(second.Id)).GetValueOrThrow();

        Assert.True(second.Replaced);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Null(job.CompletedAt);
        Assert.Equal(0, job.ProcessedCount);
        Assert.Equal(createdAt, job.CreatedAt);
        Assert.Equal("70", job.Arguments);
    }

    [PostgresFact]
    public async Task A_running_job_stays_running_its_attempt_is_refused_and_it_runs_again_with_the_new_arguments_when_its_lease_ends()
    {
        await Client().EnqueueOrReplaceAsync(NewJob("product-7", "90"));
        var running = (await LeaseAsync("w1", Noon))!;

        var second = (await Client().EnqueueOrReplaceAsync(NewJob("product-7", "70"))).GetValueOrThrow();
        var heldByOldWorker = await Store.HeartbeatAsync(running, Noon, Noon.AddMinutes(5), CancellationToken.None);
        var completedByOldWorker = await Store.CompleteAsync(running, Noon, CancellationToken.None);
        var beforeLeaseEnds = await LeaseAsync("w2", Noon.AddMinutes(1));
        var afterLeaseEnds = await LeaseAsync("w2", running.LeaseExpiresAt!.Value.AddSeconds(1));

        Assert.True(second.Replaced);
        Assert.Equal(Hold.Lost, heldByOldWorker);
        Assert.False(completedByOldWorker);
        Assert.Null(beforeLeaseEnds);
        Assert.Equal(running.Id, afterLeaseEnds!.Id);
        Assert.Equal("70", afterLeaseEnds.Arguments);
        Assert.Equal(1, afterLeaseEnds.AttemptCount);
    }

    [PostgresFact]
    public async Task A_planned_job_with_the_same_key_is_never_replaced()
    {
        await InsertRowAsync("a", JobMode.Recurring, JobStatus.Pending, Noon, key: "product-7", type: "delivery");

        var result = await Client().EnqueueOrReplaceAsync(NewJob("product-7", "70"));

        Assert.Equal(SyncErrorCodes.JobAlreadyExists, result.Error!.Code);
        Assert.Equal("recurring", await ScalarAsync<string>($"SELECT mode FROM {Schema}.jobs"));
        Assert.Null(await ScalarAsync<string>($"SELECT arguments FROM {Schema}.jobs"));
    }

    [PostgresFact]
    public async Task Without_a_dedup_key_every_call_creates_a_job()
    {
        var first = (await Client().EnqueueOrReplaceAsync(NewJob(key: null, "90"))).GetValueOrThrow();
        var second = (await Client().EnqueueOrReplaceAsync(NewJob(key: null, "70"))).GetValueOrThrow();

        Assert.False(second.Replaced);
        Assert.NotEqual(first.Id, second.Id);
    }

    [PostgresFact]
    public async Task Two_calls_for_one_key_at_the_same_moment_leave_one_job_and_both_succeed()
    {
        var results = await Task.WhenAll(
            Client().EnqueueOrReplaceAsync(NewJob("product-7", "90")),
            Client().EnqueueOrReplaceAsync(NewJob("product-7", "70")));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(results, result => !result.Value!.Replaced);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM {Schema}.jobs"));
    }

    [PostgresFact]
    public async Task EnqueueAsync_still_refuses_a_key_that_exists()
    {
        await Client().EnqueueOrReplaceAsync(NewJob("product-7", "90"));

        var result = await Client().EnqueueAsync(NewJob("product-7", "70"));

        Assert.Equal(SyncErrorCodes.JobAlreadyExists, result.Error!.Code);
    }

    [PostgresFact]
    public async Task Arguments_that_cannot_be_used_are_refused_as_they_are_by_EnqueueAsync()
    {
        var result = await Client().EnqueueOrReplaceAsync(NewJob("product-7", "ab\0cd"));

        Assert.Equal(SyncErrorCodes.ArgumentsInvalid, result.Error!.Code);
    }

    private static NewJob NewJob(string? key, string? arguments) =>
        new() { Queue = Queue, Owner = new JobOwner("a"), Type = "delivery", DedupKey = key, Arguments = arguments };

    private SyncLaneClient Client()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue.Handle<ScriptedHandler>("delivery"));
        return new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock);
    }
}
