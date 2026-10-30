using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class ClientTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task An_undeclared_queue_is_a_not_found_result_on_every_call()
    {
        var client = Client();
        var owner = new JobOwner("a");

        SyncResult[] results =
        [
            await client.PlanAsync("missing", owner),
            await client.EnqueueAsync(new NewJob { Queue = "missing", Owner = owner, Type = "orders" }),
            await client.RequeueAsync("missing"),
            await client.CancelAsync("missing", "a", "gone"),
            await client.GetProgressAsync("missing", "a")
        ];

        Assert.All(results, result =>
        {
            Assert.False(result.Succeeded);
            Assert.Equal((SyncErrorCodes.QueueNotDeclared, SyncErrorKind.NotFound), (result.Error!.Code, result.Error.Kind));
        });
    }

    [PostgresFact]
    public async Task Enqueueing_returns_the_new_id_and_a_conflict_for_the_same_key()
    {
        var client = Client();
        var job = new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders", DedupKey = "backfill-1" };

        var first = await client.EnqueueAsync(job);
        var second = await client.EnqueueAsync(job);
        var unknownType = await client.EnqueueAsync(job with { Type = "invoices" });

        Assert.Equal(Assert.Single(await JobsAsync()).Id, first.GetValueOrThrow());
        Assert.Equal((SyncErrorCodes.JobAlreadyExists, SyncErrorKind.Conflict), (second.Error!.Code, second.Error.Kind));
        Assert.Contains("backfill-1", second.Error.Message);
        Assert.Equal(SyncErrorCodes.JobTypeNotDeclared, unknownType.Error!.Code);
    }

    [PostgresFact]
    public async Task Pages_follow_the_cursor_until_the_last_one_and_count_only_when_asked()
    {
        var client = Client();
        await EnqueueAsync(Noon, Enumerable.Range(1, 5).Select(i => Planned("a", key: $"{i}")).ToArray());

        var first = await client.GetJobsAsync(new JobQuery { Queue = Queue, Limit = 2, IncludeTotal = true });
        var second = await client.GetJobsAsync(new JobQuery { Queue = Queue, Limit = 2, Cursor = first.NextCursor });
        var third = await client.GetJobsAsync(new JobQuery { Queue = Queue, Limit = 2, Cursor = second.NextCursor });

        Assert.Equal(["5", "4"], first.Items.Select(job => job.DedupKey));
        Assert.Equal(["3", "2"], second.Items.Select(job => job.DedupKey));
        Assert.Equal(["1"], third.Items.Select(job => job.DedupKey));
        Assert.Equal((5L, (long?)null), (first.Total!.Value, second.Total));
        Assert.True(first.HasMore && second.HasMore);
        Assert.False(third.HasMore);
    }

    [PostgresFact]
    public async Task A_bad_limit_or_cursor_is_a_validation_result()
    {
        var client = Client();

        var tooMany = await client.GetJobsAsync(new JobQuery { Limit = JobQuery.MaxLimit + 1 });
        var none = await client.GetJobsAsync(new JobQuery { Limit = 0 });
        var forged = await client.GetJobsAsync(new JobQuery { Cursor = "1 OR 1=1" });

        Assert.Equal(SyncErrorCodes.LimitOutOfRange, tooMany.Error!.Code);
        Assert.Equal(SyncErrorCodes.LimitOutOfRange, none.Error!.Code);
        Assert.Equal((SyncErrorCodes.CursorInvalid, SyncErrorKind.Validation), (forged.Error!.Code, forged.Error.Kind));
    }

    private SyncLaneClient Client()
    {
        var options = new SyncLaneOptions().AddQueue(Queue, queue => queue.Handle<ProductHandler>("orders"));
        return new SyncLaneClient(options, Store, new QueuePlanner(Store, _clock), _clock);
    }
}
