using SyncLane;

namespace Demo.Shop;

/// <summary>
/// Stands in for someone else's API. It answers slowly, now and then says "slow down" or fails,
/// and one page of one account cannot be read until it has been asked for several times.
/// </summary>
internal sealed class PretendShop(TimeProvider time)
{
    public const string BrokenAccount = "globex";

    private const int PageSize = 50;
    private const int ProductCount = 120;
    private const int BrokenPageFailures = 6;
    private const string BrokenPageCursor = "50";
    private const double ThrottleShare = 0.02;
    private const double FailureShare = 0.03;
    private const int FastestAnswerMs = 250;
    private const int SlowestAnswerMs = 600;

    private static readonly TimeSpan HistoryStep = TimeSpan.FromHours(3);
    private static readonly TimeSpan LiveStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BrokenSliceAge = TimeSpan.FromDays(45);
    private static readonly TimeSpan ThrottleWait = TimeSpan.FromSeconds(3);

    private readonly DateTimeOffset _openedAt = time.GetUtcNow();
    private int _brokenPageFailuresLeft = BrokenPageFailures;

    public async Task<ShopPage<ShopProduct>> GetProductsAsync(string account, string? cursor, CancellationToken cancellationToken)
    {
        await AnswerAsync(cancellationToken);

        var offset = Offset(cursor);
        var products = Enumerable.Range(offset + 1, Math.Min(PageSize, ProductCount - offset))
            .Select(number => new ShopProduct($"SKU-{number:0000}", $"Product {number} of {account}"))
            .ToList();

        return Page(products, offset, ProductCount);
    }

    public async Task<ShopPage<ShopOrder>> GetOrdersAsync(
        string account, DateTimeOffset from, DateTimeOffset to, string? cursor, CancellationToken cancellationToken)
    {
        await AnswerAsync(cancellationToken);

        if (IsBrokenPage(account, from, to, cursor) && Interlocked.Decrement(ref _brokenPageFailuresLeft) >= 0)
            throw new InvalidOperationException("The shop returned a page that could not be read.");

        var moments = Moments(from, to).ToList();
        var offset = Offset(cursor);
        var orders = moments.Skip(offset).Take(PageSize).Select(Order).ToList();

        return Page(orders, offset, moments.Count);
    }

    private static async Task AnswerAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Random.Shared.Next(FastestAnswerMs, SlowestAnswerMs), cancellationToken);

        var roll = Random.Shared.NextDouble();
        if (roll < ThrottleShare)
            throw new JobThrottledException("429 Too Many Requests", ThrottleWait);

        if (roll < ThrottleShare + FailureShare)
            throw new HttpRequestException("503 Service Unavailable");
    }

    private bool IsBrokenPage(string account, DateTimeOffset from, DateTimeOffset to, string? cursor)
    {
        var inside = _openedAt - BrokenSliceAge;
        return account == BrokenAccount && cursor == BrokenPageCursor && from <= inside && inside < to;
    }

    private IEnumerable<DateTimeOffset> Moments(DateTimeOffset from, DateTimeOffset to)
    {
        var historyEnd = to < _openedAt ? to : _openedAt;
        for (var moment = RoundUp(from, HistoryStep); moment < historyEnd; moment += HistoryStep)
            yield return moment;

        var liveStart = from > _openedAt ? from : _openedAt;
        for (var moment = RoundUp(liveStart, LiveStep); moment < to; moment += LiveStep)
            yield return moment;
    }

    private static DateTimeOffset RoundUp(DateTimeOffset moment, TimeSpan step)
    {
        var remainder = moment.UtcTicks % step.Ticks;
        return remainder == 0 ? moment.ToUniversalTime() : new DateTimeOffset(moment.UtcTicks - remainder + step.Ticks, TimeSpan.Zero);
    }

    private static ShopOrder Order(DateTimeOffset placedAt)
    {
        var seconds = placedAt.ToUnixTimeSeconds();
        return new ShopOrder(seconds.ToString(), placedAt, 10 + seconds % 9000 / 100m);
    }

    private static int Offset(string? cursor) => cursor is null ? 0 : int.Parse(cursor);

    private static ShopPage<TItem> Page<TItem>(List<TItem> items, int offset, int total)
    {
        var next = offset + items.Count;
        return new ShopPage<TItem>(items, next < total ? next.ToString() : null);
    }
}
