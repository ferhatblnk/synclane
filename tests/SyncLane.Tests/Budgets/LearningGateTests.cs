using SyncLane.Storage;
using SyncLane.Tests.Support;
using Xunit.Abstractions;

namespace SyncLane.Tests.Budgets;

/// <summary>
/// The gate for learning rates: against an API whose limit SyncLane is not told, fewer than 1% of requests are throttled and
/// throughput reaches at least 80% of the hidden limit. Each run is twenty simulated minutes of four jobs at 150 ms
/// latency, driven through the real statements with the clock passed in, so it takes seconds, not minutes.
/// </summary>
public class LearningGateTests(ITestOutputHelper output) : PostgresTest
{
    private const string Key = "shared-account";
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(20);

    public static TheoryData<string> Scenarios => new()
    {
        "unknown limit, started at half of it",
        "declared four times too high",
        "a bucket of 40 leaking 2 a second, declared four times too high",
        "another app spends 40% of it",
        "the limit halves halfway",
        "a window of 40 a minute without headers, declared four times too high"
    };

    [PostgresTheory]
    [MemberData(nameof(Scenarios))]
    public async Task Learning_keeps_throttles_under_one_percent_and_reaches_eighty_percent_of_a_hidden_limit(string scenario)
    {
        var (server, hidden, budget) = scenario switch
        {
            "unknown limit, started at half of it" => (new BucketServer(2, 2) as IServer, 2.0, Budget(8, start: 0.125)),
            "declared four times too high" => (new BucketServer(2, 2), 2.0, Budget(8)),
            "a bucket of 40 leaking 2 a second, declared four times too high" => (new BucketServer(2, 40), 2.0, Budget(8)),
            "another app spends 40% of it" => (new BucketServer(2, 2, neighbour: 0.8), 1.2, Budget(2)),
            "the limit halves halfway" => (new BucketServer(2, 2, halveAfter: Duration / 2), 1.5, Budget(2)),
            _ => (new WindowServer(40, TimeSpan.FromMinutes(1)), 40 / 60.0, Budget(160, per: TimeSpan.FromMinutes(1)))
        };
        var store = new BudgetStore(Store.DataSource, new BudgetSql(Schema));

        var (served, throttled) = await new Simulation(store, budget, server, workers: 4, latency: TimeSpan.FromMilliseconds(150)).RunAsync(Duration);

        var throughput = served / Duration.TotalSeconds / hidden;
        var share = (double)throttled / (served + throttled);
        output.WriteLine($"{scenario}: {throughput:P1} of the hidden limit, {share:P2} throttled ({served} served, {throttled} throttled)");
        Assert.True(share < 0.01, $"{share:P2} of requests were throttled.");
        Assert.True(throughput >= 0.8, $"Throughput was {throughput:P1} of the hidden limit.");
    }

    private static BudgetDescription Budget(int limit, double start = 1, TimeSpan? per = null) =>
        new("shop", limit, per ?? TimeSpan.FromSeconds(1), 1, start);

    /// <summary>
    /// Jobs that send one request after another, as the engine runs them: take a token, wait for it, check that no
    /// pause voided it (as <see cref="RateBudgets.WaitAsync"/> does), send, and after a throttle wait for the pause.
    /// </summary>
    private sealed class Simulation(BudgetStore store, BudgetDescription budget, IServer server, int workers, TimeSpan latency)
    {
        private readonly PriorityQueue<Func<Task>, DateTimeOffset> _events = new();
        private DateTimeOffset _now = Noon;
        private int _served;
        private int _throttled;

        public async Task<(int Served, int Throttled)> RunAsync(TimeSpan duration)
        {
            for (var worker = 0; worker < workers; worker++)
                At(_now, TakeAsync);

            var end = Noon + duration;

            // Sequential: a simulation runs its events in the order of their time.
            while (_events.TryDequeue(out var next, out var at) && at <= end)
            {
                _now = at;
                await next();
            }

            return (_served, _throttled);
        }

        private void At(DateTimeOffset at, Func<Task> action) => _events.Enqueue(action, at);

        private async Task TakeAsync()
        {
            var token = await store.ReserveAsync(budget, Key, _now, TimeSpan.FromDays(1), CancellationToken.None);
            At(_now + token.Wait, async () =>
            {
                if (token.Wait > TimeSpan.Zero && await store.GenerationAsync(budget.Name, Key, CancellationToken.None) != token.Generation)
                {
                    await TakeAsync();
                    return;
                }

                At(_now + latency / 2, () =>
                {
                    var (ok, retryAfter) = server.Take(_now - Noon);
                    At(_now + latency / 2, () => AnsweredAsync(token, ok, retryAfter));
                    return Task.CompletedTask;
                });
            });
        }

        private async Task AnsweredAsync(Reservation token, bool ok, TimeSpan? retryAfter)
        {
            if (ok)
            {
                _served++;
                await TakeAsync();
                return;
            }

            _throttled++;
            var pause = await store.PauseAsync(
                budget, Key, token.Generation, token.Sequence, _now, retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), CancellationToken.None);
            At(pause.Until > _now ? pause.Until : _now, TakeAsync);
        }
    }

    private interface IServer
    {
        (bool Ok, TimeSpan? RetryAfter) Take(TimeSpan at);
    }

    /// <summary>A token bucket the client is not told about, optionally shared with another app, or halved partway.</summary>
    private sealed class BucketServer(double rate, double size, double neighbour = 0, TimeSpan? halveAfter = null) : IServer
    {
        private readonly double _size = size;
        private double _tokens = size;
        private TimeSpan _refilledAt;
        private TimeSpan _neighbourAt;

        public (bool, TimeSpan?) Take(TimeSpan at)
        {
            // Sequential: the other app's requests up to now, each at its own time. It keeps asking until it is served,
            // so it is served first, from the next refill if the bucket is empty: what is left for us is the rest.
            while (neighbour > 0 && _neighbourAt <= at)
            {
                Refill(_neighbourAt);
                _tokens--;
                _neighbourAt += TimeSpan.FromSeconds(1 / neighbour);
            }

            Refill(at);
            if (_tokens < 1)
                return (false, null);

            _tokens--;
            return (true, null);
        }

        private void Refill(TimeSpan to)
        {
            var current = to >= halveAfter ? rate / 2 : rate;
            _tokens = Math.Min(_size, _tokens + (to - _refilledAt).TotalSeconds * current);
            _refilledAt = to;
        }
    }

    /// <summary>A window that resets on the clock and says nothing about it.</summary>
    private sealed class WindowServer(int quota, TimeSpan window) : IServer
    {
        private long _window = -1;
        private int _left;

        public (bool, TimeSpan?) Take(TimeSpan at)
        {
            var current = at.Ticks / window.Ticks;
            if (current != _window)
            {
                _window = current;
                _left = quota;
            }

            if (_left <= 0)
                return (false, null);

            _left--;
            return (true, null);
        }
    }
}
