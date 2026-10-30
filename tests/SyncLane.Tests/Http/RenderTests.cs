using System.Text.Json;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class RenderTests
{
    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_and_window_are_escaped_into_the_path_and_the_query()
    {
        var recipe = Compile(new HttpRecipeRequest
        {
            Url = "https://api.example.com/v1/shops/{{owner.key}}/orders?fixed=1",
            Query = new Dictionary<string, string> { ["from"] = "{{window.start}}", ["to"] = "{{window.end}}", ["q"] = "a b&c" }
        });

        using var request = Render(recipe, Job("shop/7 x"));

        Assert.Equal(
            "https://api.example.com/v1/shops/shop%2F7%20x/orders?fixed=1&from=2026-10-01T00%3A00%3A00Z&to=2026-10-08T00%3A00%3A00Z&q=a%20b%26c",
            request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData(WindowFormat.Iso8601, "2026-10-01T00:00:00Z")]
    [InlineData(WindowFormat.Unix, "1790812800")]
    [InlineData(WindowFormat.UnixMilliseconds, "1790812800000")]
    [InlineData(WindowFormat.Date, "2026-10-01")]
    public void Every_window_format_is_written_in_utc(WindowFormat format, string expected)
    {
        var inIstanbul = new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal(expected, RequestRenderer.Format(inIstanbul, format));
    }

    [Fact]
    public async Task A_body_value_is_written_as_a_json_string_and_cannot_change_the_shape_of_the_body()
    {
        var body = JsonDocument.Parse("""{ "shop": "{{owner.key}}", "range": ["{{window.start}}", "{{window.end}}"], "limit": 100 }""").RootElement;
        var recipe = Compile(new HttpRecipeRequest { Method = "POST", Url = "https://api.example.com/search", Body = body });
        const string hostile = "x\", \"admin\": true, \"y\": \"";

        using var request = Render(recipe, Job(hostile));
        var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;

        Assert.Equal(hostile, sent.GetProperty("shop").GetString());
        Assert.False(sent.TryGetProperty("admin", out _));
        Assert.Equal("2026-10-01T00:00:00Z", sent.GetProperty("range")[0].GetString());
        Assert.Equal(100, sent.GetProperty("limit").GetInt32());
        Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void Headers_are_filled_and_a_content_type_applies_to_the_body()
    {
        var body = JsonDocument.Parse("{}").RootElement;
        var recipe = Compile(new HttpRecipeRequest
        {
            Method = "POST",
            Url = "https://api.example.com/search",
            Body = body,
            Headers = new Dictionary<string, string> { ["X-Shop"] = "{{owner.key}}", ["Content-Type"] = "application/vnd.example+json" }
        });

        using var request = Render(recipe, Job("shop-7"));

        Assert.Equal("shop-7", Assert.Single(request.Headers.GetValues("X-Shop")));
        Assert.Equal("application/vnd.example+json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void A_line_break_in_a_filled_header_stops_the_request()
    {
        var recipe = Compile(new HttpRecipeRequest
        {
            Url = "https://api.example.com/orders",
            Headers = new Dictionary<string, string> { ["X-Shop"] = "{{owner.key}}" }
        });

        var result = RequestRenderer.Render(recipe, Job("shop-7\r\nX-Admin: true"));

        Assert.Equal(HttpErrorCodes.RenderedValueInvalid, result.Error!.Code);
        Assert.DoesNotContain("X-Admin", result.Error.Message);
    }

    [Fact]
    public void A_recipe_that_uses_the_window_needs_a_job_that_has_one()
    {
        var recipe = Compile(new HttpRecipeRequest { Url = "https://api.example.com/orders?from={{window.start}}" });

        var result = RequestRenderer.Render(recipe, Job("shop-7") with { WindowStart = null, WindowEnd = null });

        Assert.Equal((HttpErrorCodes.WindowMissing, SyncErrorKind.Configuration), (result.Error!.Code, result.Error.Kind));
    }

    [Fact]
    public void A_recipe_without_window_placeholders_runs_for_a_job_without_a_window()
    {
        var recipe = Compile(new HttpRecipeRequest { Url = "https://api.example.com/products" });

        using var request = Render(recipe, Job("shop-7") with { WindowStart = null, WindowEnd = null });

        Assert.Equal("https://api.example.com/products", request.RequestUri!.AbsoluteUri);
    }

    private static CompiledRecipe Compile(HttpRecipeRequest request) =>
        CompiledRecipe.Compile(new HttpRecipe { Name = "test", Request = request }).GetValueOrThrow();

    private static HttpRequestMessage Render(CompiledRecipe recipe, Job job) => RequestRenderer.Render(recipe, job).GetValueOrThrow();

    private static Job Job(string owner) => new()
    {
        Id = 1,
        Queue = "shops",
        Owner = owner,
        Lane = owner,
        Type = "orders",
        Mode = JobMode.Initial,
        Status = JobStatus.Running,
        WindowStart = October,
        WindowEnd = October.AddDays(7)
    };
}
