using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class RateTests
{
    [Theory]
    [InlineData("""{ "budget": "shop" }""")]
    [InlineData("""{ "budget": "shop", "key": "shared-account" }""")]
    [InlineData("""{ "budget": "shop/eu", "key": "eu-{{owner.key}}" }""")]
    public void A_rate_section_is_read_from_json(string rate)
    {
        var result = HttpRecipe.Parse(Recipe(rate));

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
    }

    [Theory]
    [InlineData("""{ "budget": "two words" }""", "rate.budget must be 1 to 100")]
    [InlineData("""{ "budget": "shop", "key": "{{window.start}}" }""", "rate.key may only use {{owner.key}}")]
    [InlineData("""{ "budget": "shop", "key": "{{owner.key" }""", "rate.key must be text")]
    [InlineData("""{ "budget": "shop", "key": " " }""", "rate.key must be text")]
    public void A_rate_section_that_cannot_work_is_refused_with_the_field_named(string rate, string expected)
    {
        var error = Assert.Single(HttpRecipe.Parse(Recipe(rate)).Errors);

        Assert.Equal(HttpErrorCodes.RateInvalid, error.Code);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task A_recipe_that_draws_from_an_undeclared_budget_stops_the_host_at_start_up()
    {
        var options = new SyncLaneOptions().AddQueue("shops", queue => queue.Handle<HttpRecipeHandler>("orders"));
        var http = new HttpSourceOptions();
        http.Recipe("orders", Recipe("""{ "budget": "shop" }""")).WriteTo((_, _) => Task.CompletedTask);

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() =>
            new RecipeCheck([options], new RecipeRegistry(http.Bindings)).StartingAsync(CancellationToken.None));

        Assert.Equal(HttpErrorCodes.BudgetMissing, thrown.Error.Code);
    }

    internal static string Recipe(string rate) =>
        $$"""{ "name": "test/orders", "request": { "url": "https://api.example.com/orders" }, "records": "$.items", "rate": {{rate}} }""";
}
