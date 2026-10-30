using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SyncLane.Budgets;
using SyncLane.Telemetry;

namespace SyncLane;

/// <summary>
/// The rate budgets declared with <see cref="SyncLaneOptions.AddBudget"/>: one token bucket per budget and key, shared
/// by every job in this process. Call <see cref="WaitAsync"/> before each request to the API, and
/// <see cref="Throttled"/> when the API answers "slow down", so every job on the key slows down, not only that one.
/// </summary>
/// <remarks>
/// Buckets live in memory, so each instance keeps its own. With several instances, give each a share of the limit until
/// budgets are shared through the database.
/// </remarks>
public sealed partial class RateBudgets
{
    private readonly ConcurrentDictionary<(string Budget, string Key), TokenBucket> _buckets = new();
    private readonly Dictionary<string, BudgetDescription> _declared;
    private readonly TimeSpan _maxWait;
    private readonly TimeProvider _time;
    private readonly SyncLaneMetrics _metrics;
    private readonly ILogger<RateBudgets> _logger;

    internal RateBudgets(SyncLaneOptions options, TimeProvider time, SyncLaneMetrics metrics, ILogger<RateBudgets> logger)
    {
        _declared = options.DescribeBudgets().ToDictionary(budget => budget.Name, StringComparer.Ordinal);
        _maxWait = options.MaxBudgetWait;
        _time = time;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Waits until <paramref name="budget"/> has a token for <paramref name="key"/>, and takes it. A wait longer than
    /// <see cref="SyncLaneOptions.MaxBudgetWait"/> throws <see cref="JobPostponedException"/> instead: let it propagate,
    /// and the job gives its slot back without using an attempt and runs again when the token is due.
    /// </summary>
    /// <param name="budget">The budget's name, as declared with <see cref="SyncLaneOptions.AddBudget"/>.</param>
    /// <param name="key">Whose bucket: usually the job's owner, or a key shared by owners with one set of credentials.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    public async ValueTask WaitAsync(string budget, string key, CancellationToken cancellationToken = default)
    {
        var bucket = Bucket(budget, key);
        var deadline = _time.GetUtcNow() + _maxWait;
        var waited = TimeSpan.Zero;

        // Sequential: a reservation that a pause made void is made again, behind the pause.
        while (true)
        {
            var now = _time.GetUtcNow();
            var left = deadline > now ? deadline - now : TimeSpan.Zero;
            var reservation = bucket.Reserve(now, left);
            if (!reservation.Granted)
                throw new JobPostponedException(SyncLaneErrors.BudgetWaitTooLong(budget, reservation.Wait, _maxWait).ToString(), reservation.Wait);

            if (reservation.Wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(reservation.Wait, _time, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    bucket.Return(reservation);
                    throw;
                }

                waited += reservation.Wait;
            }

            if (bucket.Holds(reservation))
            {
                _metrics.BudgetWaited(budget, waited);
                return;
            }
        }
    }

    /// <summary>
    /// Pauses <paramref name="budget"/> for <paramref name="key"/> because the API asked to slow down. Every request of
    /// the key waits until the pause ends, then the bucket refills from empty. Returns how long the pause lasts: use it
    /// as the wait of the job that got the answer.
    /// </summary>
    /// <param name="budget">The budget's name.</param>
    /// <param name="key">Whose bucket.</param>
    /// <param name="retryAfter">
    /// The wait the API asked for, from <c>Retry-After</c> or a header of its own. Without one, the pause starts at the
    /// time of one token, at least a second, and doubles while the API keeps saying "slow down", up to five minutes.
    /// </param>
    public TimeSpan Throttled(string budget, string key, TimeSpan? retryAfter = null)
    {
        var pause = Bucket(budget, key).Pause(_time.GetUtcNow(), retryAfter);
        _metrics.BudgetPaused(budget);
        LogPaused(budget, pause);
        return pause;
    }

    private TokenBucket Bucket(string budget, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_declared.TryGetValue(budget, out var declared))
            throw new SyncLaneException(SyncLaneErrors.BudgetNotDeclared(budget));

        return _buckets.GetOrAdd(
            (budget, key),
            static (_, state) => new TokenBucket(state.Declared.Limit, state.Declared.Per, state.Declared.Burst, state.Time.GetUtcNow()),
            (Declared: declared, Time: _time));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Budget {Budget} is paused for {Pause} because the API asked to slow down. Every job that draws from it waits.")]
    private partial void LogPaused(string budget, TimeSpan pause);
}
