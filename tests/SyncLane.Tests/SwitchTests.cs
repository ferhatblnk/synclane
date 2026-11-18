using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncLane.Instances;
using SyncLane.Monitoring;
using SyncLane.Storage;
using SyncLane.Tests.Support;

namespace SyncLane.Tests;

/// <summary>The switch in the database: workers and the planner turned on and off without a deploy.</summary>
public class SwitchTests : PostgresTest
{
    private const string Application = "shop";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [PostgresFact]
    public async Task Workers_switched_off_hand_their_running_job_back_at_once_and_finish_it_when_switched_on_again()
    {
        using var host = Host(runWorkers: true);
        await host.StartAsync();
        await host.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(CancellationToken.None);
        var gate = host.Services.GetRequiredService<SlowPages>();

        await host.Services.GetRequiredService<ISyncLane>().EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });
        await gate.SecondPageStarted.Task.WaitAsync(Patience);

        await SwitchAsync(workers: false);
        await AnnounceAsync();
        var handedBack = await WaitForAsync(job => job.Status == JobStatus.Pending && job.LeasedBy is null);
        var roles = host.Services.GetRequiredService<RoleSwitch>();
        var whileOff = roles.Workers;

        gate.Release();
        await SwitchAsync(workers: true);
        await Registry(host).HeartbeatAsync(CancellationToken.None);
        var completed = await WaitForAsync(job => job.Status == JobStatus.Completed);
        await host.StopAsync();

        Assert.Equal(("page-2", 0), (handedBack.Cursor, handedBack.AttemptCount));
        Assert.False(whileOff);
        Assert.Equal(3, completed.ProcessedCount);
    }

    [PostgresFact]
    public async Task An_application_whose_settings_turn_workers_off_takes_jobs_once_the_switch_turns_them_on()
    {
        using var host = Host(runWorkers: false);
        await host.StartAsync();
        host.Services.GetRequiredService<SlowPages>().Release();
        await host.Services.GetRequiredService<ISyncLane>().EnqueueAsync(new NewJob { Queue = Queue, Owner = new JobOwner("a"), Type = "orders" });

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var whileOff = (await JobsAsync()).Single();
        await SwitchAsync(workers: true);
        await Registry(host).HeartbeatAsync(CancellationToken.None);
        var completed = await WaitForAsync(job => job.Status == JobStatus.Completed);
        await host.StopAsync();

        Assert.Equal((JobStatus.Pending, (string?)null), (whileOff.Status, whileOff.LeasedBy));
        Assert.Equal(3, completed.ProcessedCount);
    }

    [PostgresFact]
    public async Task A_switch_announced_for_another_application_or_role_changes_nothing_here()
    {
        using var host = Host(runWorkers: true);
        await host.StartAsync();
        await host.Services.GetRequiredService<JobChangeFeed>().ListeningAsync(CancellationToken.None);
        await ExecuteAsync(
            $"INSERT INTO {Schema}.switches (application, role, run_workers, changed_at) VALUES ('billing', '', false, now()), ('{Application}', 'worker', false, now())");

        await AnnounceAsync("billing", "");
        await AnnounceAsync(Application, "worker");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var workers = host.Services.GetRequiredService<RoleSwitch>().Workers;
        await host.StopAsync();

        Assert.True(workers);
    }

    private static InstanceRegistry Registry(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<InstanceRegistry>().Single();

    private Task<int> SwitchAsync(bool workers) =>
        ExecuteAsync(
            $"INSERT INTO {Schema}.switches (application, role, run_workers, changed_at) VALUES ('{Application}', '', {workers}, now()) " +
            "ON CONFLICT (application, role) DO UPDATE SET run_workers = excluded.run_workers");

    private Task<int> AnnounceAsync(string application = Application, string role = "") =>
        ExecuteAsync(
            $"SELECT pg_notify('{ChangeChannel.For(Schema)}', json_build_object('control', 'switch', 'application', '{application}', 'role', '{role}')::text)");

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

    private IHost Host(bool runWorkers)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<SlowPages>();
        builder.Services.AddSyncLane(options =>
        {
            options.ConnectionString = ConnectionString!;
            options.ListenConnectionString = ListenConnectionString;
            options.Schema = Schema;
            options.Application = Application;
            options.RunPlanner = false;
            options.RunWorkers = runWorkers;
            options.PollInterval = TimeSpan.FromMilliseconds(100);
            options.AddQueue(Queue, queue => queue.Handle<SlowPagesHandler>("orders"));
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
