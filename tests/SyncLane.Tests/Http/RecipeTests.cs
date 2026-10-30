using System.Text.Json;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class RecipeTests
{
    private const string Complete =
        """
        {
          // Comments and trailing commas are allowed, so a recipe can explain itself.
          "$schema": "https://example.com/recipe.v1.json",
          "name": "example-shop/orders",
          "request": {
            "url": "https://api.example-shop.com/v2/shops/{{owner.key}}/orders",
            "query": { "updated_after": "{{window.start}}", "updated_before": "{{window.end}}", "size": "200" },
            "headers": { "Accept": "application/json" },
          },
          "records": "$.content",
          "window": { "format": "unix" }
        }
        """;

    [Fact]
    public void A_complete_recipe_is_read_from_json()
    {
        var recipe = HttpRecipe.Parse(Complete).GetValueOrThrow();

        Assert.Equal("example-shop/orders", recipe.Name);
        Assert.Equal("GET", recipe.Request.Method);
        Assert.Equal("200", recipe.Request.Query["size"]);
        Assert.Equal("$.content", recipe.Records);
        Assert.Equal(WindowFormat.Unix, recipe.Window!.Format);
    }

    [Fact]
    public void Fields_left_out_of_the_json_take_their_defaults()
    {
        var recipe = HttpRecipe.Parse("""{ "name": "minimal", "request": { "url": "https://api.example.com/items" } }""").GetValueOrThrow();

        Assert.Equal("GET", recipe.Request.Method);
        Assert.Empty(recipe.Request.Query);
        Assert.Empty(recipe.Request.Headers);
        Assert.Null(recipe.Request.Body);
        Assert.False(recipe.Request.AllowHttp);
        Assert.Null(recipe.Records);
        Assert.Null(recipe.Window);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var result = HttpRecipe.Parse(
            """
            {
              "name": "broken",
              "request": {
                "method": "DELETE",
                "url": "http://api.example.com/orders",
                "query": { "from": "{{window.begin}}" },
                "headers": { "Bad Header": "x" }
              },
              "records": "data.items"
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            [HttpErrorCodes.MethodUnsupported, HttpErrorCodes.UrlInvalid, HttpErrorCodes.HeaderInvalid, HttpErrorCodes.PlaceholderUnknown, HttpErrorCodes.RecordsPathInvalid],
            result.Errors.Select(error => error.Code));
        Assert.All(result.Errors, error => Assert.Equal(SyncErrorKind.Validation, error.Kind));
        Assert.Contains("{{window.begin}}", result.Errors.Single(error => error.Code == HttpErrorCodes.PlaceholderUnknown).Message);
    }

    [Theory]
    [InlineData("https://{{owner.key}}.example.com/orders")]
    [InlineData("https://api.example.com:{{owner.key}}/orders")]
    [InlineData("{{owner.key}}://api.example.com/orders")]
    public void A_placeholder_can_never_choose_the_host(string url)
    {
        Assert.Equal(HttpErrorCodes.PlaceholderInHost, Code(Recipe(url)));
    }

    [Theory]
    [InlineData("api.example.com/orders", "is not an absolute URL")]
    [InlineData("http://api.example.com/orders", "must use https")]
    [InlineData("ftp://api.example.com/orders", "must use https")]
    [InlineData("https://user:secret@api.example.com/orders", "must not contain a user name or password")]
    public void The_url_must_be_absolute_https_and_free_of_credentials(string url, string reason)
    {
        var result = Recipe(url).Validate();

        Assert.Equal(HttpErrorCodes.UrlInvalid, result.Error!.Code);
        Assert.Contains(reason, result.Error.Message);
        Assert.DoesNotContain("secret", result.Error.Message);
    }

    [Fact]
    public void Plain_http_is_allowed_only_when_asked_for()
    {
        var recipe = new HttpRecipe { Name = "local", Request = new HttpRecipeRequest { Url = "http://localhost:5000/orders", AllowHttp = true } };

        Assert.True(recipe.Validate().Succeeded);
    }

    [Fact]
    public void A_misspelt_field_is_refused_by_name()
    {
        var result = HttpRecipe.Parse("""{ "name": "x", "request": { "url": "https://api.example.com" }, "recrods": "$.items" }""");

        Assert.Equal(HttpErrorCodes.RecipeJsonInvalid, result.Error!.Code);
        Assert.Contains("recrods", result.Error.Message);
    }

    [Theory]
    [InlineData("""{ "request": { "url": "https://api.example.com" } }""")]
    [InlineData("""{ "name": "x", "request": { "url": "https://api.example.com" }, "window": { "format": "weekly" } }""")]
    [InlineData("""{ "name": "x", """)]
    [InlineData("null")]
    public void Json_that_is_not_a_recipe_is_refused(string json)
    {
        Assert.Equal(HttpErrorCodes.RecipeJsonInvalid, HttpRecipe.Parse(json).Error!.Code);
    }

    [Fact]
    public void A_body_needs_post()
    {
        var body = JsonDocument.Parse("""{ "from": "{{window.start}}" }""").RootElement;
        var recipe = Recipe("https://api.example.com/search") with { Request = new HttpRecipeRequest { Url = "https://api.example.com/search", Body = body } };

        Assert.Equal(HttpErrorCodes.BodyNotAllowed, Code(recipe));
        Assert.True((recipe with { Request = recipe.Request with { Method = "post" } }).Validate().Succeeded);
    }

    [Theory]
    [InlineData("https://api.example.com/orders?from={{window.start")]
    [InlineData("https://api.example.com/orders/{{}}")]
    public void An_unclosed_or_empty_placeholder_is_refused(string url)
    {
        Assert.Equal(HttpErrorCodes.TemplateMalformed, Code(Recipe(url)));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("content-length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("X Bad")]
    public void Headers_that_decide_where_the_request_goes_or_are_not_tokens_are_refused(string header)
    {
        var recipe = Recipe("https://api.example.com/orders");
        recipe = recipe with { Request = recipe.Request with { Headers = new Dictionary<string, string> { [header] = "x" } } };

        Assert.Equal(HttpErrorCodes.HeaderInvalid, Code(recipe));
    }

    [Fact]
    public void A_header_value_is_never_repeated_in_a_message()
    {
        var recipe = Recipe("https://api.example.com/orders");
        recipe = recipe with { Request = recipe.Request with { Headers = new Dictionary<string, string> { ["X-Api-Key"] = "secret-123\r\nInjected: 1" } } };

        var error = recipe.Validate().Error!;

        Assert.Equal(HttpErrorCodes.HeaderInvalid, error.Code);
        Assert.Contains("X-Api-Key", error.Message);
        Assert.DoesNotContain("secret-123", error.Message);
    }

    [Fact]
    public void An_unknown_window_format_set_in_code_is_refused()
    {
        var recipe = Recipe("https://api.example.com/orders") with { Window = new HttpRecipeWindow { Format = (WindowFormat)42 } };

        Assert.Equal(HttpErrorCodes.WindowFormatUnknown, Code(recipe));
    }

    private static HttpRecipe Recipe(string url) => new() { Name = "test", Request = new HttpRecipeRequest { Url = url } };

    private static string? Code(HttpRecipe recipe) => recipe.Validate().Error?.Code;
}
