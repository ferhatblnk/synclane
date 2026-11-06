using System.Text.Json;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

/// <summary>Values per owner and per request in a recipe, and a host made from the owner that never leaves its list.</summary>
public class OwnerValueTests
{
    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] Regions = ["api-na.example.com", "api-eu.example.com"];

    [Fact]
    public void An_owner_s_values_are_escaped_into_the_path_the_query_a_header_and_the_body()
    {
        var body = JsonDocument.Parse("""{ "market": "{{owner.market}}" }""").RootElement;
        var recipe = Compile(new HttpRecipeRequest
        {
            Method = "POST",
            Url = "https://api.example.com/v1/{{owner.market}}/orders",
            Query = new Dictionary<string, string> { ["market"] = "{{owner.market}}" },
            Headers = new Dictionary<string, string> { ["X-Market"] = "{{owner.market}}" },
            Body = body
        });

        using var request = Render(recipe, Job(new() { ["market"] = "a b/c" }));

        Assert.Equal("https://api.example.com/v1/a%20b%2Fc/orders?market=a%20b%2Fc", request.RequestUri!.AbsoluteUri);
        Assert.Equal("a b/c", request.Headers.GetValues("X-Market").Single());
        Assert.Equal(["market"], recipe.OwnerValues);
    }

    [Fact]
    public void A_value_the_owner_does_not_have_sends_nothing_and_names_the_value()
    {
        var recipe = Compile(new HttpRecipeRequest { Url = "https://api.example.com/v1/orders?market={{owner.market}}" });

        var withNone = RequestRenderer.Render(recipe, Job(null));
        var withOthers = RequestRenderer.Render(recipe, Job(new() { ["region"] = "region-value-7" }));

        Assert.All([withNone, withOthers], result =>
        {
            Assert.Equal(HttpErrorCodes.OwnerValueMissing, Assert.Single(result.Errors).Code);
            Assert.Contains("'market'", result.Errors[0].Message);
            Assert.DoesNotContain("region-value-7", result.Errors[0].Message);
        });
    }

    [Fact]
    public void A_request_id_is_new_for_every_request_and_the_same_wherever_it_appears_in_one()
    {
        var recipe = Compile(new HttpRecipeRequest
        {
            Url = "https://api.example.com/v1/orders",
            Query = new Dictionary<string, string> { ["trace"] = "{{request.id}}" },
            Headers = new Dictionary<string, string> { ["X-Correlation-Id"] = "{{request.id}}" }
        });

        using var first = Render(recipe, Job(null));
        using var second = Render(recipe, Job(null));

        var id = first.Headers.GetValues("X-Correlation-Id").Single();
        Assert.True(Guid.TryParse(id, out _));
        Assert.EndsWith($"trace={id}", first.RequestUri!.Query, StringComparison.Ordinal);
        Assert.NotEqual(id, second.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public void A_host_made_from_the_owner_goes_to_the_listed_host_it_names()
    {
        var recipe = Compile(new HttpRecipeRequest { Url = "https://{{owner.endpoint}}/v1/orders", Hosts = Regions });

        using var request = Render(recipe, Job(new() { ["endpoint"] = "api-eu.example.com" }));

        Assert.Equal("https://api-eu.example.com/v1/orders", request.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("api-xx.example.com")]
    [InlineData("api-eu.example.com.attacker.example")]
    [InlineData("attacker.example/api-eu.example.com")]
    [InlineData("attacker.example#")]
    [InlineData("user@api-eu.example.com")]
    [InlineData("api-eu.example.com:8443")]
    [InlineData("")]
    public void A_host_that_is_not_listed_is_never_sent_to(string endpoint)
    {
        var recipe = Compile(new HttpRecipeRequest { Url = "https://{{owner.endpoint}}/v1/orders", Hosts = Regions });

        var result = RequestRenderer.Render(recipe, Job(new() { ["endpoint"] = endpoint }));

        Assert.Equal(HttpErrorCodes.HostNotAllowed, Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData("shop-1", true)]
    [InlineData("SHOP-1", true)]
    [InlineData("a.shop-1", false)]
    [InlineData("", false)]
    public void A_wildcard_allows_one_name_under_its_domain_and_nothing_deeper(string shop, bool allowed)
    {
        var recipe = Compile(new HttpRecipeRequest { Url = "https://{{owner.shop}}.stores.example.com/v1/orders", Hosts = ["*.stores.example.com"] });

        var result = RequestRenderer.Render(recipe, Job(new() { ["shop"] = shop }));

        Assert.Equal(allowed, result.Succeeded);
        result.Value?.Dispose();
    }

    [Theory]
    [InlineData("https://{{owner.endpoint}}/v1/orders", null, HttpErrorCodes.PlaceholderInHost)]
    [InlineData("https://api.example.com/v1/orders", new[] { "api.example.com" }, HttpErrorCodes.UrlInvalid)]
    [InlineData("https://{{owner.endpoint}}/v1/orders", new[] { "*.com" }, HttpErrorCodes.UrlInvalid)]
    [InlineData("https://{{owner.endpoint}}/v1/orders", new[] { "https://api.example.com" }, HttpErrorCodes.UrlInvalid)]
    [InlineData("https://{{owner.endpoint}}/v1/orders", new string[0], HttpErrorCodes.UrlInvalid)]
    [InlineData("https://{{owner.endpoint}}:8443/v1/orders", new[] { "api.example.com" }, HttpErrorCodes.UrlInvalid)]
    [InlineData("https://{{window.start}}.example.com/v1/orders", new[] { "*.example.com" }, HttpErrorCodes.UrlInvalid)]
    [InlineData("https://user@{{owner.endpoint}}/v1/orders", new[] { "api.example.com" }, HttpErrorCodes.UrlInvalid)]
    public void A_made_host_needs_a_list_of_host_names_and_only_the_owner_s_values(string url, string[]? hosts, string code)
    {
        var compiled = CompiledRecipe.Compile(new HttpRecipe { Name = "test", Request = new HttpRecipeRequest { Url = url, Hosts = hosts } });

        Assert.Contains(compiled.Errors, error => error.Code == code);
    }

    [Fact]
    public void A_next_page_on_another_listed_host_than_the_request_s_is_refused()
    {
        var recipe = CompiledRecipe.Compile(new HttpRecipe
        {
            Name = "test",
            Request = new HttpRecipeRequest { Url = "https://{{owner.endpoint}}/v1/orders", Hosts = Regions },
            Pagination = new HttpRecipePagination { Type = PaginationType.NextUrl, From = "$.next" }
        }).GetValueOrThrow();

        var same = Next(recipe, "https://api-eu.example.com/v1/orders?page=2");
        var other = Next(recipe, "https://api-na.example.com/v1/orders?page=2");

        Assert.Equal("https://api-eu.example.com/v1/orders?page=2", same.GetValueOrThrow());
        Assert.Equal(HttpErrorCodes.NextUrlForeignHost, Assert.Single(other.Errors).Code);
    }

    [Fact]
    public void A_rate_key_made_from_an_owner_s_value_puts_owners_of_one_account_in_one_bucket()
    {
        var recipe = CompiledRecipe.Compile(new HttpRecipe
        {
            Name = "test",
            Request = new HttpRecipeRequest { Url = "https://api.example.com/v1/orders" },
            Rate = new HttpRecipeRate { Budget = "shop", Key = "seller:{{owner.seller}}" }
        }).GetValueOrThrow();

        var first = recipe.Rate!.KeyOf(Job(new() { ["seller"] = "s-1" }) with { Owner = "company-1:market-a" }, "test");
        var second = recipe.Rate.KeyOf(Job(new() { ["seller"] = "s-1" }) with { Owner = "company-1:market-b" }, "test");
        var missing = recipe.Rate.KeyOf(Job(null), "test");

        Assert.Equal(("seller:s-1", "seller:s-1"), (first.GetValueOrThrow(), second.GetValueOrThrow()));
        Assert.Equal(HttpErrorCodes.OwnerValueMissing, Assert.Single(missing.Errors).Code);
    }

    [Theory]
    [InlineData("{{window.start}}")]
    [InlineData("{{request.id}}")]
    public void A_rate_key_from_anything_but_the_owner_is_refused(string key)
    {
        var compiled = CompiledRecipe.Compile(new HttpRecipe
        {
            Name = "test",
            Request = new HttpRecipeRequest { Url = "https://api.example.com/v1/orders" },
            Rate = new HttpRecipeRate { Budget = "shop", Key = key }
        });

        Assert.Contains(compiled.Errors, error => error.Code == HttpErrorCodes.RateInvalid && error.Message.Contains("rate.key", StringComparison.Ordinal));
    }

    private static SyncResult<string?> Next(CompiledRecipe recipe, string next)
    {
        using var document = JsonDocument.Parse($$"""{ "items": [], "next": "{{next}}" }""");
        var root = document.RootElement;
        return recipe.Paginator.Next(null, new PageAnswer(root, root.GetProperty("items"), [], new Uri("https://api-eu.example.com/v1/orders")));
    }

    private static CompiledRecipe Compile(HttpRecipeRequest request) =>
        CompiledRecipe.Compile(new HttpRecipe { Name = "test", Request = request }).GetValueOrThrow();

    private static HttpRequestMessage Render(CompiledRecipe recipe, Job job) => RequestRenderer.Render(recipe, job).GetValueOrThrow();

    private static Job Job(Dictionary<string, string>? values) => new()
    {
        Id = 1,
        Queue = "shops",
        Owner = "shop-7",
        OwnerValues = values,
        Lane = "shop-7",
        Type = "orders",
        Mode = JobMode.Initial,
        Status = JobStatus.Running,
        WindowStart = October,
        WindowEnd = October.AddDays(7)
    };
}
