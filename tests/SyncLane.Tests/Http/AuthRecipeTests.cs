using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;

namespace SyncLane.Tests.Http;

public class AuthRecipeTests
{
    [Theory]
    [InlineData("""{ "type": "bearer", "credential": "example-shop" }""")]
    [InlineData("""{ "type": "basic", "credential": "example-shop" }""")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "name": "X-Api-Key" }""")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "in": "header", "name": "Authorization", "prefix": "Token " }""")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "in": "query", "name": "api_key" }""")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "https://auth.example.com/token", "scope": "orders:read items:read", "clientAuth": "body" }""")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "refreshToken", "tokenUrl": "https://auth.example.com/token" }""")]
    [InlineData("""{ "type": "signed", "credential": "example-shop/eu", "signer": "example-hmac" }""")]
    public void Every_auth_type_is_read_from_json(string auth)
    {
        var result = Parse(auth);

        Assert.True(result.Succeeded, string.Join('\n', result.Errors));
        Assert.NotNull(result.Value!.Auth);
    }

    [Theory]
    [InlineData("""{ "type": "bearer", "credential": "example-shop", "name": "X-Api-Key" }""", "auth.name does not apply to type bearer")]
    [InlineData("""{ "type": "basic", "credential": "example-shop", "tokenUrl": "https://auth.example.com/token" }""", "auth.tokenUrl does not apply to type basic")]
    [InlineData("""{ "type": "bearer", "credential": "example-shop", "signer": "example-hmac" }""", "auth.signer does not apply to type bearer")]
    [InlineData("""{ "type": "bearer", "credential": "" }""", "auth.credential must be 1 to 200")]
    [InlineData("""{ "type": "bearer", "credential": "two words" }""", "auth.credential must be 1 to 200")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop" }""", "auth.name is required for type apiKey")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "in": "query", "name": "api_key", "prefix": "Token " }""", "auth.prefix applies only to a key in a header")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "name": "Host" }""", "'Host' is not a header SyncLane can set")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "name": "Content-Type" }""", "'Content-Type' is not a header SyncLane can set")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "name": "X Key" }""", "'X Key' is not a header SyncLane can set")]
    [InlineData("""{ "type": "apiKey", "credential": "example-shop", "name": "X-Api-Key", "prefix": "Token\n" }""", "auth.prefix must be printable ASCII")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "tokenUrl": "https://auth.example.com/token" }""", "auth.grant is required for type oauth2")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials" }""", "auth.tokenUrl is required for type oauth2")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "http://auth.example.com/token" }""", "auth.tokenUrl must use https")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "/token" }""", "auth.tokenUrl is not an absolute URL")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "https://auth.example.com/{{owner.key}}/token" }""", "auth.tokenUrl must not contain placeholders")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "https://id:secret@auth.example.com/token" }""", "auth.tokenUrl must not contain a user name or password")]
    [InlineData("""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "https://auth.example.com/token", "scope": "a\nb" }""", "auth.scope must be printable ASCII")]
    [InlineData("""{ "type": "signed", "credential": "example-shop" }""", "auth.signer is required for type signed")]
    public void An_auth_that_cannot_work_is_refused_with_the_field_named(string auth, string expected)
    {
        var error = Assert.Single(Parse(auth).Errors);

        Assert.Equal(HttpErrorCodes.AuthInvalid, error.Code);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void An_auth_type_that_does_not_exist_is_refused_when_read()
    {
        Assert.Equal(HttpErrorCodes.RecipeJsonInvalid, Parse("""{ "type": "jwt", "credential": "example-shop" }""").Error?.Code);
    }

    [Fact]
    public void An_api_key_in_the_query_may_not_clash_with_the_query_or_the_paging()
    {
        const string key = """{ "type": "apiKey", "credential": "example-shop", "in": "query", "name": "key" }""";

        var query = Assert.Single(Parse(key, query: """{ "key": "x" }""").Errors);
        var paging = Assert.Single(Parse(key, pagination: """{ "type": "cursor", "param": "key", "from": "$.next" }""").Errors);

        Assert.Contains("auth.name 'key' is also set in request.query", query.Message);
        Assert.Contains("auth.name 'key' is also a pagination parameter", paging.Message);
    }

    [Fact]
    public void An_api_key_in_a_header_may_not_clash_with_the_recipes_headers()
    {
        var error = Assert.Single(Parse(
            """{ "type": "apiKey", "credential": "example-shop", "name": "X-Api-Key" }""", headers: """{ "x-api-key": "other" }""").Errors);

        Assert.Contains("auth.name 'X-Api-Key' is also set in request.headers", error.Message);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Proxy-Authorization")]
    public void A_recipe_never_holds_a_credential_header_and_the_message_never_repeats_it(string header)
    {
        var result = HttpRecipe.Parse(
            $$"""{ "name": "test", "request": { "url": "https://api.example.com/orders", "headers": { "{{header}}": "Bearer live-secret-123" } } }""");

        var error = Assert.Single(result.Errors);
        Assert.Equal(HttpErrorCodes.CredentialInRecipe, error.Code);
        Assert.DoesNotContain("live-secret-123", error.Message);
    }

    [Fact]
    public void A_recipe_with_auth_needs_a_credential_store()
    {
        var thrown = Assert.Throws<SyncLaneException>(() => new ServiceCollection().AddSyncLaneHttp(http =>
            http.Recipe("orders", Recipe("""{ "type": "bearer", "credential": "example-shop" }""")).WriteTo((_, _) => Task.CompletedTask)));

        Assert.Equal(HttpErrorCodes.CredentialStoreMissing, thrown.Error.Code);
    }

    [Fact]
    public void A_signed_recipe_needs_its_signer_and_a_signer_name_is_registered_once()
    {
        var thrown = Assert.Throws<SyncLaneException>(() => new ServiceCollection().AddSyncLaneHttp(http =>
        {
            http.Credentials<VaultStore>();
            http.Signer<TestSigner>("other").Signer<MovingSigner>("other");
            http.Recipe("orders", Recipe("""{ "type": "signed", "credential": "example-shop", "signer": "example-hmac" }""")).WriteTo((_, _) => Task.CompletedTask);
        }));

        Assert.Equal([HttpErrorCodes.OptionInvalid, HttpErrorCodes.SignerMissing], thrown.Errors.Select(error => error.Code));
    }

    [Fact]
    public void A_credential_never_shows_its_values_when_printed()
    {
        var credential = new Credential { Token = "tok-123", ClientSecret = "sec-456", Values = new Dictionary<string, string> { ["k"] = "val-789" } };

        var text = $"{credential}";

        Assert.Equal("Credential (Token, ClientSecret, Values set; values hidden)", text);
        Assert.Equal("Credential (empty)", new Credential().ToString());
    }

    [Theory]
    [InlineData("https://api.example.com/a?x=1", "key", "v/+", "https://api.example.com/a?x=1&key=v%2F%2B")]
    [InlineData("https://api.example.com/a?key=old&x=%2F", "key", "new", "https://api.example.com/a?x=%2F&key=new")]
    [InlineData("https://api.example.com/a", "key", "v", "https://api.example.com/a?key=v")]
    public void A_query_parameter_is_set_and_the_rest_of_the_url_is_kept_as_written(string url, string name, string value, string expected)
    {
        Assert.Equal(expected, QueryString.With(new Uri(url), name, value).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://api.example.com/a?page=2&api_key=s3cret", "https://api.example.com/a?page=2")]
    [InlineData("https://api.example.com/a?api%5Fkey=s3cret&page=2", "https://api.example.com/a?page=2")]
    [InlineData("https://api.example.com/a?api_key=s3cret", "https://api.example.com/a")]
    [InlineData("https://api.example.com/a?page=2", "https://api.example.com/a?page=2")]
    public void A_query_parameter_is_removed_however_it_is_escaped(string url, string expected)
    {
        Assert.Equal(expected, QueryString.Without(new Uri(url), "api_key").AbsoluteUri);
    }

    private static SyncResult<HttpRecipe> Parse(string auth, string query = "{}", string headers = "{}", string? pagination = null) =>
        HttpRecipe.Parse(Recipe(auth, query, headers, pagination));

    private static string Recipe(string auth, string query = "{}", string headers = "{}", string? pagination = null) =>
        $$"""
        {
          "name": "test/orders",
          "request": { "url": "https://api.example.com/orders", "query": {{query}}, "headers": {{headers}} },
          "records": "$.items",
          {{(pagination is null ? "" : $"\"pagination\": {pagination},")}}
          "auth": {{auth}}
        }
        """;
}
