using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using SyncLane.Telemetry;

namespace SyncLane.Tests.Budgets;

public class RateBudgetsTests
{
    [Fact]
    public void A_budget_is_declared_once_with_settings_that_can_work()
    {
        var twice = Assert.Throws<SyncLaneException>(() => new SyncLaneOptions()
            .AddBudget("shop", 1, TimeSpan.FromSeconds(1))
            .AddBudget("shop", 2, TimeSpan.FromSeconds(1)));

        Assert.Equal(SyncErrorCodes.BudgetDeclaredTwice, twice.Error.Code);
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("shop", 0, TimeSpan.FromSeconds(1))));
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("shop", 1, TimeSpan.Zero)));
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("shop", 1, TimeSpan.FromSeconds(1), burst: 0)));
        Assert.Equal(SyncErrorCodes.BudgetSettingInvalid, Invalid(options => options.AddBudget("two words", 1, TimeSpan.FromSeconds(1))));
        Assert.Equal(SyncErrorCodes.IntervalNotPositive, Invalid(options => options.MaxBudgetWait = TimeSpan.Zero));
    }

    [Fact]
    public async Task A_budget_that_was_never_declared_is_a_configuration_error()
    {
        var budgets = Budgets(_ => { });

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => budgets.WaitAsync("shop", "owner-1").AsTask());

        Assert.Equal(SyncErrorCodes.BudgetNotDeclared, thrown.Error.Code);
    }

    [Fact]
    public async Task Waiters_on_one_key_are_spaced_at_the_rate_and_other_keys_have_buckets_of_their_own()
    {
        var budgets = Budgets(options => options.AddBudget("shop", 10, TimeSpan.FromSeconds(1)));
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => budgets.WaitAsync("shop", "owner-1").AsTask()));
        var spaced = clock.Elapsed;
        clock.Restart();
        await budgets.WaitAsync("shop", "owner-2");

        Assert.InRange(spaced.TotalSeconds, 0.27, 2);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 0.05);
    }

    [Fact]
    public async Task A_throttle_pauses_every_request_of_the_key_and_no_other()
    {
        var budgets = Budgets(options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1), burst: 10));

        var pause = budgets.Throttled("shop", "owner-1", TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        await budgets.WaitAsync("shop", "owner-2");
        var other = clock.Elapsed;
        await budgets.WaitAsync("shop", "owner-1");

        Assert.Equal(TimeSpan.FromMilliseconds(300), pause);
        Assert.InRange(other.TotalSeconds, 0, 0.05);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.29, 2);
    }

    [Fact]
    public async Task A_wait_longer_than_max_budget_wait_postpones_the_job_instead()
    {
        var budgets = Budgets(options =>
        {
            options.MaxBudgetWait = TimeSpan.FromMilliseconds(100);
            options.AddBudget("reports", 1, TimeSpan.FromMinutes(1));
        });
        await budgets.WaitAsync("reports", "owner-1");

        var thrown = await Assert.ThrowsAsync<JobPostponedException>(() => budgets.WaitAsync("reports", "owner-1").AsTask());

        Assert.StartsWith(SyncErrorCodes.BudgetWaitTooLong, thrown.Message);
        Assert.InRange(thrown.Delay.TotalSeconds, 59, 60);
    }

    [Fact]
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

        var budgets = Budgets(options => options.AddBudget("shop", 100, TimeSpan.FromSeconds(1)));
        await budgets.WaitAsync("shop", "customer-42");
        budgets.Throttled("shop", "customer-42", TimeSpan.Zero);

        Assert.Contains("synclane.budget.wait synclane.budget=shop", seen);
        Assert.Contains("synclane.budget.paused synclane.budget=shop", seen);
        Assert.All(seen, measurement => Assert.DoesNotContain("customer-42", measurement));
    }

    private static void Record(ConcurrentQueue<string> seen, Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var text = instrument.Name;
        foreach (var (key, value) in tags)
            text += $" {key}={value}";

        seen.Enqueue(text);
    }

    private static string Invalid(Action<SyncLaneOptions> configure)
    {
        var options = new SyncLaneOptions { ConnectionString = "Host=unused" };
        configure(options);
        return Assert.Throws<SyncLaneException>(options.Validate).Error.Code;
    }

    private static RateBudgets Budgets(Action<SyncLaneOptions> configure)
    {
        var options = new SyncLaneOptions { ConnectionString = "Host=unused" };
        configure(options);
        options.Validate();
        return new RateBudgets(options, TimeProvider.System, new SyncLaneMetrics(), NullLogger<RateBudgets>.Instance);
    }
}
