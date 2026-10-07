using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

public class WorkerTests : PostgresTest
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [PostgresFact]
    public async Task The_host_plans_and_runs_everything_and_keeps_each_lane_to_one_job_at_a_time()
    {
        var probe = new LaneProbe();
        JobOwner[] owners =
        [
            new("store-1", "seller-a"), new("store-2", "seller-a"),
            new("store-3", "seller-b"), new("store-4", "seller-b"),
            new("store-5"), new("store-6")
        ];

        using var host = Host(
            services => services.AddSingleton(probe).AddSingleton(new FixedOwners(owners)),
            queue =>
            {
                queue.Concurrency = 4;
                queue.Owners<FixedOwners>();
                queue.Snapshot<ProbeHandler>("products");
                queue.Windowed<ProbeHandler>("orders", TimeSpan.FromDays(90), TimeSpan.FromDays(30));
            });

        await host.StartAsync();
        var jobs = await WaitAsync(all => all.Count == 24 && all.All(job => job.Status == JobStatus.Completed));
        var progress = await host.Services.GetRequiredService<ISyncLane>().GetProgressAsync(Queue, "store-1");
        await host.StopAsync();

        Assert.Equal(1, probe.MostPerLane);
        Assert.InRange(probe.MostOverall, 2, 4);
        Assert.All(jobs, job => Assert.Equal((10L, 1), (job.ProcessedCount, job.AttemptCount)));
        Assert.Equal(new OwnerProgress(4, 4, 0, 0), progress);
        Assert.True(progress.IsComplete);
    }

    [PostgresFact]
    public async Task Stopping_the_host_hands_running_jobs_back_so_the_next_start_continues_them()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = Host(
            services => services.AddSingleton(started),
            queue => queue.Handle<BlockingHandler>("orders"));

        await host.StartAsync();
        var enqueued = await host.Services.GetRequiredService<ISyncLane>()
            .EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("store-1"), Type = "orders" });
        await started.Task.WaitAsync(Patience);
        await host.StopAsync();

        var job = Assert.Single(await JobsAsync());
        Assert.True(enqueued);
        Assert.Equal((JobMode.Manual, JobStatus.Pending, BlockingHandler.SecondPage, 0), (job.Mode, job.Status, job.Cursor, job.AttemptCount));
    }

    private IHost Host(Action<IServiceCollection> register, Action<QueueOptions> configure)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        register(builder.Services);

        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.Schema = Schema;
            options.PlanInterval = TimeSpan.FromMilliseconds(200);
            options.PollInterval = TimeSpan.FromMilliseconds(50);
            options.AddQueue(Queue, configure);
        });

        return builder.Build();
    }

    private async Task<IReadOnlyList<Job>> WaitAsync(Func<IReadOnlyList<Job>, bool> done)
    {
        using var timeout = new CancellationTokenSource(Patience);

        while (true)
        {
            var jobs = await JobsAsync();
            if (done(jobs))
                return jobs;

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }
}
