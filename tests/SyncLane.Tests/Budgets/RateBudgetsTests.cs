using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SyncLane.Storage;
using SyncLane.Telemetry;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Budgets;

/// <summary>Budgets as handlers use them, on real time, with each <see cref="RateBudgets"/> standing for one instance.</summary>
public class RateBudgetsTests : PostgresTest
{
    private readonly List<NpgsqlDataSource> _instances = [];

    [PostgresFact]
    public async Task Waiters_on_one_key_are_spaced_at_the_rate_and_other_keys_have_buckets_of_their_own()
    {
        var budgets = Instance(options => options.AddBudget("shop", 10, TimeSpan.FromSeconds(1)));
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => budgets.WaitAsync("shop", "owner-1").AsTask()));
        var spaced = clock.Elapsed;
        clock.Restart();
        await budgets.WaitAsync("shop", "owner-2");

        Assert.InRange(spaced.TotalSeconds, 0.27, 2);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 0.1);
    }

    [PostgresFact]
    public async Task Instances_sharing_a_budget_are_spaced_as_one()
    {
        var first = Instance(options => options.AddBudget("shop", 10, TimeSpan.FromSeconds(1)));
        var second = Instance(options => options.AddBudget("shop", 10, TimeSpan.FromSeconds(1)));
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(index => (index % 2 == 0 ? first : second).WaitAsync("shop", "owner-1").AsTask()));

        Assert.InRange(clock.Elapsed.TotalSeconds, 0.47, 2);
    }

    [PostgresFact]
    public async Task A_throttle_on_one_instance_pauses_every_request_of_the_key_on_every_instance_and_no_other_key()
    {
        var first = Instance(options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10));
        var second = Instance(options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10));
        var permit = await first.WaitAsync("shop", "owner-1");

        var pause = await first.ThrottledAsync(permit, TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        await second.WaitAsync("shop", "owner-2");
        var other = clock.Elapsed;
        await second.WaitAsync("shop", "owner-1");

        Assert.InRange(pause.TotalMilliseconds, 290, 300);
        Assert.InRange(other.TotalSeconds, 0, 0.1);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.25, 2);
    }

    [PostgresFact]
    public async Task A_token_one_instance_waits_for_is_void_after_another_pauses_the_budget_and_is_taken_again_behind_the_pause()
    {
        var first = Instance(options => options.AddBudget("shop", 5, TimeSpan.FromSeconds(1)));
        var second = Instance(options => options.AddBudget("shop", 5, TimeSpan.FromSeconds(1)));
        var sent = await first.WaitAsync("shop", "owner-1");
        var clock = Stopwatch.StartNew();

        // The second instance's token is due in 200 ms. Before that, the first hears "slow down" for a second.
        var waiting = second.WaitAsync("shop", "owner-1").AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await first.ThrottledAsync(sent, TimeSpan.FromSeconds(1));
        await waiting;

        Assert.InRange(clock.Elapsed.TotalSeconds, 1.2, 3);
    }

    [PostgresFact]
    public async Task When_the_api_says_nothing_is_left_every_instance_waits_for_the_reset()
    {
        var first = Instance(options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10));
        var second = Instance(options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10));
        var permit = await first.WaitAsync("shop", "owner-1");

        await first.ReportRemainingAsync(permit, remaining: 0, resetsIn: TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();
        await second.WaitAsync("shop", "owner-1");

        Assert.InRange(clock.Elapsed.TotalSeconds, 0.35, 2);
    }

    [PostgresFact]
    public async Task A_wait_longer_than_max_budget_wait_postpones_the_job_instead()
    {
        var budgets = Instance(options =>
        {
            options.MaxBudgetWait = TimeSpan.FromMilliseconds(100);
            options.AddBudget("reports", 1, TimeSpan.FromMinutes(1));
        });
        await budgets.WaitAsync("reports", "owner-1");

        var thrown = await Assert.ThrowsAsync<JobPostponedException>(() => budgets.WaitAsync("reports", "owner-1").AsTask());

        Assert.StartsWith(SyncErrorCodes.BudgetWaitTooLong, thrown.Message);
        Assert.InRange(thrown.Delay.TotalSeconds, 59, 60);
    }

    [PostgresFact]
    public async Task The_first_pause_of_a_slow_budget_lasts_one_token_and_no_pause_lasts_longer_than_five_minutes()
    {
        var clock = new ManualClock(Noon);
        var budgets = Instance(
            options =>
            {
                options.AddBudget("slow", 1, TimeSpan.FromMinutes(1));
                options.AddBudget("hourly", 1, TimeSpan.FromHours(1));
            },
            clock);

        var slow = await budgets.ThrottledAsync(await budgets.WaitAsync("slow", "owner-1"));
        var hourly = await budgets.ThrottledAsync(await budgets.WaitAsync("hourly", "owner-1"));

        Assert.Equal(TimeSpan.FromMinutes(1), slow);
        Assert.Equal(TimeSpan.FromMinutes(5), hourly);
    }

    [PostgresFact]
    public async Task Budget_metrics_name_the_budget_and_never_the_key()
    {
        var seen = new ConcurrentQueue<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, found) =>
            {
                if (instrument.Name.StartsWith("synclane.budget", StringComparison.Ordinal))
                    found.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(seen, instrument, tags));
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(seen, instrument, tags));
        listener.Start();

        var budgets = Instance(options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1)));
        await budgets.ThrottledAsync(await budgets.WaitAsync("shop", "customer-42"), TimeSpan.Zero);
        await budgets.ReportRemainingAsync(await budgets.WaitAsync("shop", "customer-42"), 0, TimeSpan.FromMilliseconds(1));

        Assert.Contains("synclane.budget.wait synclane.budget=shop", seen);
        Assert.Contains("synclane.budget.paused synclane.budget=shop", seen);
        Assert.Contains("synclane.budget.exhausted synclane.budget=shop", seen);
        Assert.All(seen, measurement => Assert.DoesNotContain("customer-42", measurement));
    }

    public override async Task DisposeAsync()
    {
        foreach (var instance in _instances)
            await instance.DisposeAsync();

        await base.DisposeAsync();
    }

    private static void Record(ConcurrentQueue<string> seen, Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var text = instrument.Name;
        foreach (var (key, value) in tags)
            text += $" {key}={value}";

        seen.Enqueue(text);
    }

    /// <summary>A <see cref="RateBudgets"/> with a connection pool of its own, as another instance would have.</summary>
    private RateBudgets Instance(Action<SyncLaneOptions> configure, TimeProvider? time = null)
    {
        var options = new SyncLaneOptions { ConnectionString = ConnectionString!, Schema = Schema };
        configure(options);
        options.Validate();

        var source = NpgsqlDataSource.Create(ConnectionString!);
        _instances.Add(source);
        return new RateBudgets(options, new BudgetStore(source, new BudgetSql(Schema)), time ?? TimeProvider.System, new SyncLaneMetrics(), NullLogger<RateBudgets>.Instance);
    }
}
