namespace SyncLane.Http;

/// <summary>Reads the wait a 429 answer asks for.</summary>
internal static class RetryAfter
{
    /// <summary>The wait from <c>Retry-After</c>, never negative and never longer than <paramref name="max"/>; <c>null</c> without one.</summary>
    public static TimeSpan? From(HttpResponseMessage response, DateTimeOffset now, TimeSpan max)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - now : null);
        if (wait is not { } asked)
            return null;

        return asked < TimeSpan.Zero ? TimeSpan.Zero : asked > max ? max : asked;
    }
}
