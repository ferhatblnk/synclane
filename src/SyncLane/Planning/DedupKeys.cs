using System.Globalization;

namespace SyncLane.Planning;

/// <summary>The keys that make planned jobs unique for their owner and type.</summary>
internal static class DedupKeys
{
    public const string InitialSnapshot = "initial";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string InitialWindow(DateTimeOffset windowStart) => $"initial:{Format(windowStart)}";

    public static string Recurring(DateTimeOffset periodStart) => $"recurring:{Format(periodStart)}";

    public static string Loaded(DateTimeOffset start, DateTimeOffset end) => $"loaded:{Format(start)}/{Format(end)}";

    private static string Format(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
