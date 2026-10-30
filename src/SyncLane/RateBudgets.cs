using Microsoft.Extensions.Logging;
using SyncLane.Storage;
using SyncLane.Telemetry;

namespace SyncLane;

/// <summary>
/// The rate budgets declared with <see cref="SyncLaneOptions.AddBudget"/>: one token bucket per budget and key, kept in
/// the database and shared by every job of every instance. Call <see cref="WaitAsync"/> before each request to the API,
/// and <see cref="ThrottledAsync"/> when the API answers "slow down", so every job on the key slows down, not only that one.
/// </summary>
/// <remarks>
/// Each call is one round trip to the database, plus one more after a wait to check that no pause came in the meantime.
/// Instances compute refills with their own clocks, so keep them synchronised: a clock that runs ahead by a second
/// can let one second's worth of extra tokens through.
/// </remarks>
public sealed partial class RateBudgets
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxPause = TimeSpan.FromDays(1);

    private readonly Dictionary<string, BudgetDescription> _declared;
    private readonly TimeSpan _maxWait;
    private readonly BudgetStore _store;
    private readonly TimeProvider _time;
    private readonly SyncLaneMetrics _metrics;
    private readonly ILogger<RateBudgets> _logger;

    internal RateBudgets(SyncLaneOptions options, BudgetStore store, TimeProvider time, SyncLaneMetrics metrics, ILogger<RateBudgets> logger)
    {
        _declared = options.DescribeBudgets().ToDictionary(budget => budget.Name, StringComparer.Ordinal);
        _maxWait = options.MaxBudgetWait;
        _store = store;
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
    /// <returns>The permit for one request. Pass it to <see cref="ThrottledAsync"/> if the API answers "slow down".</returns>
    public async ValueTask<BudgetPermit> WaitAsync(string budget, string key, CancellationToken cancellationToken = default)
    {
        var declared = Declared(budget, key);
        var deadline = _time.GetUtcNow() + _maxWait;
        var waited = TimeSpan.Zero;

        // Sequential: a reservation that a pause made void is made again, behind the pause.
        while (true)
        {
            var now = _time.GetUtcNow();
            var left = deadline > now ? deadline - now : TimeSpan.Zero;
            var reservation = await _store.ReserveAsync(declared, key, now, left, cancellationToken);
            if (!reservation.Granted)
                throw new JobPostponedException(SyncLaneErrors.BudgetWaitTooLong(budget, reservation.Wait, _maxWait).ToString(), reservation.Wait);

            if (reservation.Wait > TimeSpan.Zero)
            {
                await Task.Delay(reservation.Wait, _time, cancellationToken);
                waited += reservation.Wait;

                // Another instance may have paused the bucket while this one waited. Then the token is void.
                if (await _store.GenerationAsync(budget, key, cancellationToken) != reservation.Generation)
                    continue;
            }

            _metrics.BudgetWaited(budget, waited);
            return new BudgetPermit(budget, key, reservation.Generation);
        }
    }

    /// <summary>
    /// Pauses the permit's budget for its key because the API answered "slow down" to the request sent with it. Every
    /// request of the key, on every instance, waits until the pause ends; then the bucket refills from empty. Returns
    /// how long the pause lasts: use it as the wait of the job that got the answer.
    /// </summary>
    /// <param name="permit">The permit the throttled request was sent with.</param>
    /// <param name="retryAfter">
    /// The wait the API asked for, from <c>Retry-After</c> or a header of its own. Without one, the pause starts at the
    /// time of one token, at least a second, and doubles while the API keeps saying "slow down", up to five minutes.
    /// </param>
    /// <param name="cancellationToken">Stops the call.</param>
    public async ValueTask<TimeSpan> ThrottledAsync(BudgetPermit permit, TimeSpan? retryAfter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permit);

        var declared = Declared(permit.Budget, permit.Key);
        var now = _time.GetUtcNow();
        var asked = retryAfter is { } wait ? Clamp(wait) : (TimeSpan?)null;
        var pause = await _store.PauseAsync(declared, permit.Key, permit.Generation, now, asked, FirstBackoff(declared), MaxBackoff, cancellationToken);
        var left = pause.Until > now ? pause.Until - now : TimeSpan.Zero;

        if (pause.Paused)
        {
            _metrics.BudgetPaused(permit.Budget);
            LogPaused(permit.Budget, left);
        }

        return left;
    }

    private BudgetDescription Declared(string budget, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _declared.TryGetValue(budget, out var declared)
            ? declared
            : throw new SyncLaneException(SyncLaneErrors.BudgetNotDeclared(budget));
    }

    /// <summary>The first pause without <c>Retry-After</c>: one token's time, but at least a second.</summary>
    private static TimeSpan FirstBackoff(BudgetDescription budget)
    {
        var oneToken = budget.Per / budget.Limit;
        return oneToken > MinBackoff ? oneToken : MinBackoff;
    }

    private static TimeSpan Clamp(TimeSpan asked) =>
        asked < TimeSpan.Zero ? TimeSpan.Zero : asked > MaxPause ? MaxPause : asked;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Budget {Budget} is paused for {Pause} because the API asked to slow down. Every job that draws from it waits.")]
    private partial void LogPaused(string budget, TimeSpan pause);
}
