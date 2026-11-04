using System.Globalization;
using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>
/// A recipe's rate headers, checked: which headers say how many requests are left and when more come, and how they are
/// written. A value that cannot be read is ignored, never reported: these headers are hints, and the IETF draft asks
/// clients to ignore malformed ones.
/// </summary>
internal sealed partial class RateHeaders(
    string? remaining, RateRemainingFormat remainingFormat, string? reset, RateResetFormat resetFormat,
    string? limit, RateLimitFormat limitFormat, TimeSpan limitWindow)
{
    /// <summary>The longest window a limit may count over: a day.</summary>
    internal const int MaxLimitWindow = 86_400;

    /// <summary>The headers, or <c>null</c> when the recipe names none or they have errors.</summary>
    public static RateHeaders? Compile(string recipe, HttpRecipeRateHeaders? headers, List<SyncError> errors)
    {
        if (headers is null)
            return null;

        var before = errors.Count;
        if (headers.Remaining is null && headers.Reset is null && headers.Limit is null)
            errors.Add(HttpErrors.RateInvalid(recipe, "headers", "must name remaining, reset, limit or several"));

        CheckName(recipe, "headers.remaining", headers.Remaining, errors);
        CheckName(recipe, "headers.reset", headers.Reset, errors);
        CheckName(recipe, "headers.limit", headers.Limit, errors);

        if (headers.LimitFormat is not null && headers.Limit is null)
            errors.Add(HttpErrors.RateInvalid(recipe, "headers.limitFormat", "needs headers.limit"));

        if (headers.LimitWindow is not null && headers.Limit is null)
            errors.Add(HttpErrors.RateInvalid(recipe, "headers.limitWindow", "needs headers.limit"));

        var limitFormat = headers.LimitFormat ?? RateLimitFormat.Number;
        if (headers.Limit is not null && limitFormat == RateLimitFormat.Number && headers.LimitWindow is not (>= 1 and <= MaxLimitWindow))
        {
            errors.Add(HttpErrors.RateInvalid(
                recipe, "headers.limitWindow", $"must be the seconds the limit counts over, 1 to {MaxLimitWindow}, as the API's documentation states it"));
        }

        if (limitFormat == RateLimitFormat.Ietf && headers.LimitWindow is not null)
            errors.Add(HttpErrors.RateInvalid(recipe, "headers.limitWindow", "must be left out with limitFormat ietf: the RateLimit-Policy field carries its own window"));

        if (headers.RemainingFormat is not null && headers.Remaining is null)
            errors.Add(HttpErrors.RateInvalid(recipe, "headers.remainingFormat", "needs headers.remaining"));

        if (headers.ResetFormat is not null && headers.Reset is null)
            errors.Add(HttpErrors.RateInvalid(recipe, "headers.resetFormat", "needs headers.reset"));

        if (headers.RemainingFormat == RateRemainingFormat.Ietf && headers.Reset is not null)
            errors.Add(HttpErrors.RateInvalid(recipe, "headers.reset", "must be left out with remainingFormat ietf: the RateLimit field carries its own window"));

        return errors.Count > before
            ? null
            : new RateHeaders(
                headers.Remaining, headers.RemainingFormat ?? RateRemainingFormat.Number, headers.Reset, headers.ResetFormat ?? RateResetFormat.Seconds,
                headers.Limit, limitFormat, TimeSpan.FromSeconds(headers.LimitWindow ?? 0));
    }

    /// <summary>
    /// The limit <paramref name="response"/> reports, as a number of requests per window, or <c>null</c> when the recipe
    /// names no limit header, or it is missing, unreadable or not positive.
    /// </summary>
    public RateLimitReading? ReadLimit(HttpResponseMessage response)
    {
        if (limit is null)
            return null;

        if (limitFormat == RateLimitFormat.Ietf)
            return Joined(response, limit) is { } field && Policy(field) is { } policy ? policy : null;

        return First(response, limit) is { } text && Number(text) is > 0 and var value ? new RateLimitReading(value, limitWindow) : null;
    }

    /// <summary>
    /// The <c>RateLimit-Policy</c> field of the IETF draft: a list of policies such as <c>"default";q=100;w=60</c>. Older
    /// drafts wrote the quota as the item itself (<c>100;w=60</c>); both are read. The tightest policy counts: the fewest
    /// requests a second. A policy without a whole positive quota and window says nothing usable and is skipped.
    /// </summary>
    internal static RateLimitReading? Policy(string field)
    {
        RateLimitReading? tightest = null;
        foreach (var item in Split(field, ','))
        {
            var parameters = Split(item, ';');
            if (parameters.Count == 0 || parameters[0].Length == 0)
                continue;

            var quota = Whole(parameters[0]);
            long? window = null;
            for (var index = 1; index < parameters.Count; index++)
            {
                var parameter = parameters[index];
                var equals = parameter.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0)
                    continue;

                var name = parameter[..equals].Trim();
                var value = parameter[(equals + 1)..].Trim();
                if (name == "q")
                    quota = Whole(value);
                else if (name == "w")
                    window = Whole(value);
            }

            if (quota is not > 0 || window is not (> 0 and <= MaxLimitWindow))
                continue;

            var reading = new RateLimitReading(quota.Value, TimeSpan.FromSeconds(window.Value));
            if (tightest is not { } best || reading.PerSecond < best.PerSecond)
                tightest = reading;
        }

        return tightest;
    }

    /// <summary>
    /// What <paramref name="response"/> says: how many requests are left, and in how long more come, never negative and
    /// never longer than <paramref name="max"/>. Either is <c>null</c> when its header is missing or cannot be read.
    /// </summary>
    public RateReading Read(HttpResponseMessage response, DateTimeOffset now, TimeSpan max)
    {
        if (remainingFormat == RateRemainingFormat.Ietf)
        {
            if (Joined(response, remaining!) is not { } field || Ietf(field) is not { } parsed)
                return default;

            return new RateReading(parsed.Remaining, parsed.Window is { } seconds ? Cap(TimeSpan.FromSeconds(seconds), max) : null);
        }

        var count = remaining is null ? null : First(response, remaining) is { } text ? Remaining(text, remainingFormat) : null;
        var resetsIn = reset is null ? null : First(response, reset) is { } value ? ResetsIn(value, resetFormat, response.Headers.Date ?? now) : null;
        return new RateReading(count, resetsIn is { } wait ? Cap(wait, max) : null);
    }

    private static double? Remaining(string text, RateRemainingFormat format)
    {
        if (format == RateRemainingFormat.Number)
            return Number(text);

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && Number(text[..slash]) is { } used && Number(text[(slash + 1)..]) is { } limit ? limit - used : null;
    }

    private static TimeSpan? ResetsIn(string text, RateResetFormat format, DateTimeOffset reference)
    {
        if (format == RateResetFormat.HttpDate)
        {
            return DateTimeOffset.TryParseExact(text.Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date - reference
                : null;
        }

        if (Number(text) is not { } number || number < 0)
            return null;

        var seconds = format switch
        {
            RateResetFormat.Seconds => number,
            RateResetFormat.Unix => number - reference.ToUnixTimeMilliseconds() / 1000d,
            _ => (number - reference.ToUnixTimeMilliseconds()) / 1000d
        };

        // A time that is absurdly far away is a misread, not a plan.
        return Math.Abs(seconds) < TimeSpan.MaxValue.TotalSeconds / 2 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>
    /// The <c>RateLimit</c> field of the IETF draft: a list of items such as <c>"default";r=50;t=30</c>. The most
    /// restrictive item counts: the fewest left, and of those the longest window. An item without a whole <c>r</c> makes
    /// the field malformed, and it is ignored.
    /// </summary>
    internal static (double Remaining, double? Window)? Ietf(string field)
    {
        (double Remaining, double? Window)? strictest = null;
        foreach (var item in Split(field, ','))
        {
            long? left = null;
            long? window = null;
            var parameters = Split(item, ';');
            if (parameters.Count == 0 || parameters[0].Length == 0)
                return null;

            for (var index = 1; index < parameters.Count; index++)
            {
                var parameter = parameters[index];
                var equals = parameter.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? parameter : parameter[..equals].Trim();
                var value = equals < 0 ? "" : parameter[(equals + 1)..].Trim();

                if (name == "r")
                    left = Whole(value) ?? -1;
                else if (name == "t")
                    window = Whole(value) ?? -1;
            }

            if (left is not >= 0 || window is < 0)
                return null;

            if (strictest is not { } best || left < best.Remaining || (left == best.Remaining && (window ?? 0) > (best.Window ?? 0)))
                strictest = (left.Value, window);
        }

        return strictest;
    }

    /// <summary>Splits a structured field at <paramref name="separator"/>, but not inside a quoted string.</summary>
    private static List<string> Split(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted && character == '\\')
                index++;
            else if (character == '"')
                quoted = !quoted;
            else if (!quoted && character == separator)
            {
                parts.Add(text[start..index].Trim());
                start = index + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    private static long? Whole(string text) =>
        text.Length <= 15 && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) ? whole : null;

    private static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    private static TimeSpan Cap(TimeSpan wait, TimeSpan max) => wait < TimeSpan.Zero ? TimeSpan.Zero : wait > max ? max : wait;

    private static string? First(HttpResponseMessage response, string name)
    {
        if (!response.Headers.NonValidated.TryGetValues(name, out var values))
            return null;

        foreach (var value in values)
            return value;

        return null;
    }

    /// <summary>Every value of a header that may be split over several lines, joined as one list.</summary>
    private static string? Joined(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static void CheckName(string recipe, string field, string? name, List<SyncError> errors)
    {
        if (name is not null && !HeaderName().IsMatch(name))
            errors.Add(HttpErrors.RateInvalid(recipe, field, "must be a header name: 1 to 100 letters, digits or !#$%&'*+-.^_`|~"));
    }

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}$")]
    private static partial Regex HeaderName();
}

/// <summary>What one answer said: how many requests are left, and in how long more come.</summary>
internal readonly record struct RateReading(double? Remaining, TimeSpan? ResetsIn);

/// <summary>The limit one answer reported: <see cref="Limit"/> requests per <see cref="Per"/>.</summary>
internal readonly record struct RateLimitReading(double Limit, TimeSpan Per)
{
    public double PerSecond => Limit / Per.TotalSeconds;
}
