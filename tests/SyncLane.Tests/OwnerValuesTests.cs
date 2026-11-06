using SyncLane.Planning;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>An owner's values: given by the owner source or an enqueue, kept with the owner, read with every job.</summary>
public class OwnerValuesTests : PostgresTest
{
    private readonly ManualClock _clock = new(Noon);

    [PostgresFact]
    public async Task Values_from_the_owner_source_travel_with_the_job_a_worker_takes_and_with_every_job_read()
    {
        var client = Client();
        await client.PlanAsync(Queue, Owner("a", ("market", "m-1"), ("region", "eu")));

        var taken = await LeaseAsync("w", Noon);
        var listed = await JobsAsync();
        var found = (await client.GetJobsAsync(new JobQuery { Queue = Queue, Search = "a" })).Items;

        var expected = new Dictionary<string, string> { ["market"] = "m-1", ["region"] = "eu" };
        Assert.Equal(expected, taken!.OwnerValues);
        Assert.All(listed, job => Assert.Equal(expected, job.OwnerValues));
        Assert.All(found, job => Assert.Equal(expected, job.OwnerValues));
    }

    [PostgresFact]
    public async Task New_values_replace_the_old_ones_and_an_owner_given_without_values_keeps_them()
    {
        var client = Client();
        await client.PlanAsync(Queue, Owner("a", ("region", "eu")));
        await client.PlanAsync(Queue, Owner("a", ("region", "na")));
        var replaced = (await JobsAsync())[0].OwnerValues;
        await client.PlanAsync(Queue, new JobOwner("a", Name: "Acme Toys"));
        var kept = (await JobsAsync())[0].OwnerValues;
        var enqueued = await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = Owner("b", ("region", "fe")), Type = "orders" });

        Assert.Equal("na", replaced!["region"]);
        Assert.Equal("na", kept!["region"]);
        Assert.Equal("fe", (await JobAsync(enqueued.GetValueOrThrow())).OwnerValues!["region"]);
    }

    [PostgresFact]
    public async Task Values_that_cannot_be_used_are_refused_by_name_and_nothing_is_written()
    {
        var client = Client();
        var secretish = "value-" + Guid.NewGuid().ToString("N");

        var badName = await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = Owner("a", ("not a name", "x")), Type = "orders" });
        var taken = await client.PlanAsync(Queue, Owner("a", ("key", "x")));
        var badValue = await client.EnqueueAsync(new NewJob { Queue = Queue, Owner = Owner("a", ("region", secretish + "\n")), Type = "orders" });
        var round = await Assert.ThrowsAsync<SyncLaneException>(() =>
            new QueuePlanner(Store, _clock).PlanAsync(Options().Queue(Queue), [Owner("a", ("region", new string('x', OwnerValues.MaxLength + 1)))], CancellationToken.None));

        SyncResult[] refused = [badName, taken, badValue];
        Assert.All(refused, result => Assert.Equal(SyncErrorCodes.OwnerValueInvalid, result.Error!.Code));
        Assert.DoesNotContain(secretish, badValue.Error!.Message);
        Assert.Contains("'region'", badValue.Error.Message);
        Assert.Equal(SyncErrorCodes.OwnerValueInvalid, Assert.Single(round.Errors).Code);
        Assert.Empty(await JobsAsync());
    }

    private static JobOwner Owner(string key, params (string Name, string Value)[] values) =>
        new(key) { Values = values.ToDictionary(value => value.Name, value => value.Value) };

    private SyncLaneOptions Options() =>
        new SyncLaneOptions().AddQueue(Queue, queue => queue.Windowed<OrderHandler>("orders", TimeSpan.FromDays(60), TimeSpan.FromDays(30)));

    private SyncLaneClient Client() => new(Options(), Store, new QueuePlanner(Store, _clock), _clock);
}
