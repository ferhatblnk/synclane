using System.Collections.Concurrent;

namespace LoadTest;

/// <summary>
/// An API that answers pages slowly, fails now and then, enforces a limit it never announces, and watches every
/// request it receives. It is the witness of the run: SyncLane's own tables say what it did, this says what an API
/// on the other side saw.
/// </summary>
internal sealed class PretendApi(Settings settings, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new();
    private long _requests;
    private long _served;
    private long _throttled;
    private long _failed;
    private long _overlaps;

    public static async Task<int> RunAsync(Settings settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var api = new PretendApi(settings, TimeProvider.System);

        app.MapGet("/accounts/{account}/orders", (string account, int? page, HttpContext http, CancellationToken cancellationToken) =>
            api.OrdersAsync(account, page ?? 0, http, cancellationToken));
        app.MapGet("/stats", api.Stats);

        await app.RunAsync(settings.ApiListen);
        return 0;
    }

    private async Task<IResult> OrdersAsync(string account, int page, HttpContext http, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        var state = _accounts.GetOrAdd(account, name => new Account(settings.IsHot(name) ? settings.HotLimit : settings.HiddenLimit, settings.HiddenBurst));

        // Two requests for one account at the same moment mean two jobs of one lane ran together.
        if (Interlocked.Increment(ref state.InFlight) > 1)
            Interlocked.Increment(ref _overlaps);

        try
        {
            if (!state.Admit(time.GetUtcNow()))
            {
                Interlocked.Increment(ref _throttled);
                http.Response.Headers.RetryAfter = "1";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var jitter = settings.LatencyMs * (0.8 + Random.Shared.NextDouble() * 0.4);
            await Task.Delay(TimeSpan.FromMilliseconds(jitter), cancellationToken);

            if (Random.Shared.NextDouble() < settings.FailureRate)
            {
                Interlocked.Increment(ref _failed);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            Interlocked.Increment(ref _served);
            var next = page + 1 < settings.Pages ? page + 1 : (int?)null;
            return Results.Json(new PageAnswer(settings.Records, next));
        }
        finally
        {
            Interlocked.Decrement(ref state.InFlight);
        }
    }

    private ApiStats Stats() =>
        new(Interlocked.Read(ref _requests), Interlocked.Read(ref _served), Interlocked.Read(ref _throttled),
            Interlocked.Read(ref _failed), Interlocked.Read(ref _overlaps), _accounts.Count,
            _accounts.IsEmpty ? 0 : _accounts.Values.Max(account => account.BusiestSecond),
            _accounts.IsEmpty ? 0 : _accounts.Values.Where(account => account.Limit == settings.HiddenLimit).DefaultIfEmpty().Max(account => account?.BusiestSecond ?? 0),
            _accounts.IsEmpty ? 0 : _accounts.Values.Where(account => account.Limit == settings.HotLimit).DefaultIfEmpty().Max(account => account?.BusiestSecond ?? 0));

    /// <summary>One account: a token bucket SyncLane is not told about, and a count of requests per second.</summary>
    private sealed class Account(int limit, int burst)
    {
        private readonly Lock _gate = new();
        private double _tokens = burst;
        private DateTimeOffset _refilled = DateTimeOffset.MinValue;
        private long _second;
        private int _inSecond;

        public int InFlight;

        public int Limit { get; } = limit;

        public int BusiestSecond { get; private set; }

        public bool Admit(DateTimeOffset now)
        {
            lock (_gate)
            {
                var second = now.ToUnixTimeSeconds();
                if (second != _second)
                {
                    _second = second;
                    _inSecond = 0;
                }

                BusiestSecond = Math.Max(BusiestSecond, ++_inSecond);

                if (_refilled != DateTimeOffset.MinValue)
                    _tokens = Math.Min(burst, _tokens + (now - _refilled).TotalSeconds * Limit);
                _refilled = now;

                if (_tokens < 1)
                    return false;

                _tokens -= 1;
                return true;
            }
        }
    }
}

/// <summary>A page: how many records it holds, and the next page, or null when this was the last.</summary>
internal sealed record PageAnswer(int Items, int? Next);

/// <summary>What the API saw over the whole run.</summary>
internal sealed record ApiStats(
    long Requests, long Served, long Throttled, long Failed, long Overlaps, int Accounts,
    int BusiestSecond, int BusiestSecondNormal, int BusiestSecondHot);
