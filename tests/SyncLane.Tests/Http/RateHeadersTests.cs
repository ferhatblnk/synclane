using System.Net;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class RateHeadersTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Theory]
    [InlineData("12", 12.0)]
    [InlineData(" 7 ", 7.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("-3", -3.0)]
    public void A_number_is_read_as_it_is(string value, double expected)
    {
        var reading = Read(Headers(remaining: "X-Left"), ("X-Left", value));

        Assert.Equal(expected, reading.Remaining);
    }

    [Fact]
    public void Used_of_a_limit_leaves_the_difference()
    {
        var reading = Read(Headers(remaining: "X-Call-Limit", remainingFormat: RateRemainingFormat.UsedOfLimit), ("X-Call-Limit", "32/40"));

        Assert.Equal(8.0, reading.Remaining);
    }

    [Theory]
    [InlineData("seconds", "41", 41.0)]
    [InlineData("seconds", "0.5", 0.5)]
    [InlineData("unix", "1793361630", 30.0)]
    [InlineData("unix-ms", "1793361600250", 0.25)]
    [InlineData("http-date", "Fri, 30 Oct 2026 12:01:00 GMT", 60.0)]
    public void A_reset_is_read_in_its_format(string format, string value, double seconds)
    {
        var headers = Headers(reset: "X-Reset", resetFormat: Format(format));

        var reading = Read(headers, ("X-Reset", value));

        Assert.Equal(TimeSpan.FromSeconds(seconds), reading.ResetsIn);
    }

    [Fact]
    public void A_reset_given_as_a_time_is_measured_from_the_answers_date_not_the_instances_clock()
    {
        var headers = Headers(reset: "X-Reset", resetFormat: RateResetFormat.Unix);
        using var response = Response(("X-Reset", "1793361630"));
        response.Headers.Date = Noon.AddSeconds(-10);

        // The instance's clock says noon; the API's says ten seconds earlier. The API's reset is 40 of its seconds away.
        var reading = headers.Read(response, Noon, Hour);

        Assert.Equal(TimeSpan.FromSeconds(40), reading.ResetsIn);
    }

    [Theory]
    [InlineData("\"default\";r=50;t=30", 50.0, 30.0)]
    [InlineData("\"default\";r=999;pk=:dHJpYWwxMjEzMjM=:", 999.0, null)]
    [InlineData("\"burst\";r=0;t=1, \"daily\";r=5000;t=3600", 0.0, 1.0)]
    [InlineData("\"hourly\";r=10;t=60, \"daily\";r=10;t=3600", 10.0, 3600.0)]
    [InlineData("\"a;b,c\";r=3;t=9", 3.0, 9.0)]
    public void The_ietf_field_gives_the_most_restrictive_item(string field, double remaining, double? window)
    {
        var reading = Read(Headers(remaining: "RateLimit", remainingFormat: RateRemainingFormat.Ietf), ("RateLimit", field));

        Assert.Equal(remaining, reading.Remaining);
        Assert.Equal(window is { } seconds ? TimeSpan.FromSeconds(seconds) : null, reading.ResetsIn);
    }

    [Theory]
    [InlineData("limit=100, remaining=50, reset=5")]
    [InlineData("\"default\";t=30")]
    [InlineData("\"default\";r=-1;t=30")]
    [InlineData("\"default\";r=1.5")]
    [InlineData("\"default\";r=50, ")]
    public void A_malformed_ietf_field_is_ignored(string field)
    {
        var reading = Read(Headers(remaining: "RateLimit", remainingFormat: RateRemainingFormat.Ietf), ("RateLimit", field));

        Assert.Equal(default, reading);
    }

    [Theory]
    [InlineData("many")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("32 of 40")]
    public void A_value_that_cannot_be_read_is_ignored(string value)
    {
        var headers = Headers(remaining: "X-Left", reset: "X-Reset");

        var reading = Read(headers, ("X-Left", value), ("X-Reset", value));

        Assert.Equal(default, reading);
    }

    [Fact]
    public void A_missing_header_reads_as_nothing_and_a_reset_in_the_past_as_now()
    {
        var headers = Headers(remaining: "X-Left", reset: "X-Reset", resetFormat: RateResetFormat.Unix);

        var reading = Read(headers, ("X-Reset", "1793361500"));

        Assert.Null(reading.Remaining);
        Assert.Equal(TimeSpan.Zero, reading.ResetsIn);
    }

    [Fact]
    public void A_reset_further_away_than_allowed_is_cut()
    {
        var reading = Read(Headers(reset: "X-Reset"), ("X-Reset", "86400"));

        Assert.Equal(Hour, reading.ResetsIn);
    }

    [Theory]
    [InlineData("""{ }""", "headers must name remaining, reset, limit or several")]
    [InlineData("""{ "limit": "X-Limit" }""", "headers.limitWindow must be the seconds the limit counts over")]
    [InlineData("""{ "limit": "X-Limit", "limitWindow": 0 }""", "headers.limitWindow must be the seconds the limit counts over")]
    [InlineData("""{ "limit": "X-Limit", "limitWindow": 86401 }""", "headers.limitWindow must be the seconds the limit counts over")]
    [InlineData("""{ "limit": "RateLimit-Policy", "limitFormat": "ietf", "limitWindow": 60 }""", "headers.limitWindow must be left out with limitFormat ietf")]
    [InlineData("""{ "remaining": "X-Left", "limitWindow": 60 }""", "headers.limitWindow needs headers.limit")]
    [InlineData("""{ "remaining": "X-Left", "limitFormat": "ietf" }""", "headers.limitFormat needs headers.limit")]
    [InlineData("""{ "remaining": "X Left" }""", "headers.remaining must be a header name")]
    [InlineData("""{ "reset": "X-Reset", "remainingFormat": "number" }""", "headers.remainingFormat needs headers.remaining")]
    [InlineData("""{ "remaining": "X-Left", "resetFormat": "unix" }""", "headers.resetFormat needs headers.reset")]
    [InlineData("""{ "remaining": "RateLimit", "remainingFormat": "ietf", "reset": "X-Reset" }""", "headers.reset must be left out with remainingFormat ietf")]
    public void Headers_that_cannot_work_are_refused_with_the_field_named(string headers, string expected)
    {
        var error = Assert.Single(HttpRecipe.Parse(RateTests.Recipe($$"""{ "budget": "shop", "headers": {{headers}} }""")).Errors);

        Assert.Equal(HttpErrorCodes.RateInvalid, error.Code);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("40", 40.0)]
    [InlineData(" 0.5 ", 0.5)]
    public void A_limit_given_as_a_number_counts_over_the_window_the_recipe_states(string value, double expected)
    {
        using var response = Response(("X-Limit", value));

        var reading = Headers(limit: "X-Limit", limitWindow: 60).ReadLimit(response);

        Assert.Equal(new RateLimitReading(expected, TimeSpan.FromMinutes(1)), reading);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    [InlineData("NaN")]
    public void A_limit_that_is_not_a_positive_number_is_ignored(string value)
    {
        using var response = Response(("X-Limit", value));

        Assert.Null(Headers(limit: "X-Limit", limitWindow: 60).ReadLimit(response));
    }

    [Fact]
    public void No_limit_is_read_when_the_recipe_names_no_limit_header_or_the_answer_lacks_it()
    {
        using var response = Response(("X-Limit", "40"), ("RateLimit-Policy", "\"default\";q=100;w=60"));
        using var empty = Response();

        Assert.Null(Headers(remaining: "X-Left").ReadLimit(response));
        Assert.Null(Headers(limit: "X-Limit", limitWindow: 60).ReadLimit(empty));
    }

    [Theory]
    [InlineData("\"default\";q=100;w=60", 100.0, 60)]
    [InlineData("\"burst\";q=10;w=1, \"daily\";q=5000;w=86400", 5000.0, 86400)]
    [InlineData("\"burst\";q=100;w=1, \"minute\";q=30;w=60", 30.0, 60)]
    [InlineData("100;w=60", 100.0, 60)]
    [InlineData("\"no-window\";q=1, \"default\";q=20;w=10", 20.0, 10)]
    [InlineData("\"a;b,c\";q=3;w=9", 3.0, 9)]
    public void The_ietf_policy_field_gives_its_tightest_policy(string field, double limit, int window)
    {
        using var response = Response(("RateLimit-Policy", field));

        var reading = Headers(limit: "RateLimit-Policy", limitFormat: RateLimitFormat.Ietf).ReadLimit(response);

        Assert.Equal(new RateLimitReading(limit, TimeSpan.FromSeconds(window)), reading);
    }

    [Theory]
    [InlineData("\"default\";q=100")]
    [InlineData("\"default\";q=0;w=60")]
    [InlineData("\"default\";q=-1;w=60")]
    [InlineData("\"default\";q=1.5;w=60")]
    [InlineData("\"default\";q=10;w=86401")]
    [InlineData("limit=100, window=60")]
    public void A_policy_field_without_a_usable_policy_is_ignored(string field)
    {
        using var response = Response(("RateLimit-Policy", field));

        Assert.Null(Headers(limit: "RateLimit-Policy", limitFormat: RateLimitFormat.Ietf).ReadLimit(response));
    }

    [Fact]
    public void An_unknown_format_is_refused_when_the_recipe_is_read()
    {
        var result = HttpRecipe.Parse(RateTests.Recipe("""{ "budget": "shop", "headers": { "reset": "X-Reset", "resetFormat": "minutes" } }"""));

        Assert.Equal(HttpErrorCodes.RecipeJsonInvalid, Assert.Single(result.Errors).Code);
    }

    private static RateReading Read(RateHeaders headers, params (string Name, string Value)[] values)
    {
        using var response = Response(values);
        return headers.Read(response, Noon, Hour);
    }

    private static HttpResponseMessage Response(params (string Name, string Value)[] values)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var (name, value) in values)
            response.Headers.TryAddWithoutValidation(name, value);

        return response;
    }

    private static RateHeaders Headers(
        string? remaining = null,
        RateRemainingFormat? remainingFormat = null,
        string? reset = null,
        RateResetFormat? resetFormat = null,
        string? limit = null,
        RateLimitFormat? limitFormat = null,
        int? limitWindow = null)
    {
        var errors = new List<SyncError>();
        var headers = RateHeaders.Compile("test", new HttpRecipeRateHeaders
        {
            Remaining = remaining,
            RemainingFormat = remainingFormat,
            Reset = reset,
            ResetFormat = resetFormat,
            Limit = limit,
            LimitFormat = limitFormat,
            LimitWindow = limitWindow
        }, errors);

        Assert.Empty(errors);
        return headers!;
    }

    private static RateResetFormat Format(string name) => name switch
    {
        "seconds" => RateResetFormat.Seconds,
        "unix" => RateResetFormat.Unix,
        "unix-ms" => RateResetFormat.UnixMilliseconds,
        _ => RateResetFormat.HttpDate
    };
}
