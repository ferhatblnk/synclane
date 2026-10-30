using System.Text;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;

namespace SyncLane.Tests.Http;

public class AuthTests
{
    [Fact]
    public async Task Bearer_sends_the_owners_token()
    {
        await using var harness = Harness("""{ "type": "bearer", "credential": "example-shop" }""");
        harness.Vault.Add("shop-7", "example-shop", new Credential { Token = "tok-7" }).Add("shop-8", "example-shop", new Credential { Token = "tok-8" });

        await harness.RunAsync("shop-7");
        await harness.RunAsync("shop-8");

        Assert.Equal(["Bearer tok-7", "Bearer tok-8"], harness.ApiRequests.Select(Authorization));
    }

    [Fact]
    public async Task Basic_sends_the_user_name_and_password_encoded_as_utf8()
    {
        await using var harness = Harness("""{ "type": "basic", "credential": "example-shop" }""");
        harness.Vault.Add("shop-7", "example-shop", new Credential { Username = "müller", Password = "pässwörd" });

        await harness.RunAsync();

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("müller:pässwörd"));
        Assert.Equal(expected, Authorization(Assert.Single(harness.ApiRequests)));
    }

    [Fact]
    public async Task Basic_allows_a_key_as_user_name_with_no_password()
    {
        await using var harness = Harness("""{ "type": "basic", "credential": "example-shop" }""");
        harness.Vault.Add("shop-7", "example-shop", new Credential { Username = "key-123" });

        await harness.RunAsync();

        Assert.Equal("Basic " + Convert.ToBase64String("key-123:"u8.ToArray()), Authorization(Assert.Single(harness.ApiRequests)));
    }

    [Fact]
    public async Task An_api_key_goes_in_the_header_the_recipe_names_after_its_prefix()
    {
        await using var harness = Harness("""{ "type": "apiKey", "credential": "example-shop", "name": "Authorization", "prefix": "Token " }""");
        harness.Vault.Add("shop-7", "example-shop", new Credential { Token = "key-123" });

        await harness.RunAsync();

        Assert.Equal("Token key-123", Authorization(Assert.Single(harness.ApiRequests)));
    }

    [Fact]
    public async Task An_api_key_in_the_query_is_escaped()
    {
        await using var harness = Harness("""{ "type": "apiKey", "credential": "example-shop", "in": "query", "name": "api_key" }""");
        harness.Vault.Add("shop-7", "example-shop", new Credential { Token = "a+b/c" });

        await harness.RunAsync();

        Assert.Equal("https://api.example.com/orders?api_key=a%2Bb%2Fc", Assert.Single(harness.ApiRequests).RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("nextUrl", """{ "type": "nextUrl", "from": "$.next" }""")]
    [InlineData("linkHeader", """{ "type": "linkHeader" }""")]
    public async Task An_api_key_the_api_copies_into_its_next_page_url_never_reaches_the_cursor(string _, string pagination)
    {
        const string next = "https://api.example.com/orders?page=2&api_key=key-123";
        await using var harness = Harness(
            """{ "type": "apiKey", "credential": "example-shop", "in": "query", "name": "api_key" }""",
            request =>
            {
                if (request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal))
                    return AuthHarness.Ok();

                var response = AuthHarness.Ok($$"""{ "items": [], "next": "{{next}}" }""");
                response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
                return response;
            },
            pagination);
        harness.Vault.Add("shop-7", "example-shop", new Credential { Token = "key-123" });

        var first = await harness.RunAsync();
        await harness.RunAsync(cursor: first.NextCursor);

        Assert.Equal("https://api.example.com/orders?page=2", first.NextCursor);
        Assert.Equal(next, harness.ApiRequests.Last().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task An_owner_without_a_credential_is_abandoned_before_anything_is_sent()
    {
        await using var harness = Harness("""{ "type": "bearer", "credential": "example-shop" }""");
        harness.Vault.Add("shop-8", "example-shop", new Credential { Token = "tok-8" });

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync("shop-7"));

        Assert.StartsWith(HttpErrorCodes.CredentialMissing, thrown.Message);
        Assert.Empty(harness.ApiRequests);
    }

    [Theory]
    [InlineData("""{ "type": "bearer", "credential": "c" }""", null, null, "has no Token")]
    [InlineData("""{ "type": "bearer", "credential": "c" }""", "tok\r\nX-Injected: secret-value", null, "has a Token with a character a header cannot carry")]
    [InlineData("""{ "type": "apiKey", "credential": "c", "name": "X-Key" }""", "clé-secret-value", null, "has a Token with a character a header cannot carry")]
    [InlineData("""{ "type": "basic", "credential": "c" }""", null, "user:secret-value", "has a Username with a colon")]
    [InlineData("""{ "type": "basic", "credential": "c" }""", null, null, "has no Username")]
    public async Task A_credential_that_cannot_be_sent_abandons_the_job_without_showing_it(string auth, string? token, string? username, string expected)
    {
        await using var harness = Harness(auth);
        harness.Vault.Add("shop-7", "c", new Credential { Token = token, Username = username });

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync());

        Assert.StartsWith(HttpErrorCodes.CredentialInvalid, thrown.Message);
        Assert.Contains(expected, thrown.Message);
        Assert.DoesNotContain("secret-value", thrown.Message);
        Assert.Empty(harness.ApiRequests);
    }

    [Fact]
    public async Task A_signer_signs_the_complete_request_with_the_owners_credential()
    {
        await using var harness = Harness(
            """{ "type": "signed", "credential": "example-shop", "signer": "example-hmac" }""",
            pagination: """{ "type": "page", "param": "page" }""",
            configure: http => http.Signer<TestSigner>("example-hmac"));
        harness.Vault.Add("shop-7", "example-shop", new Credential { Values = new Dictionary<string, string> { ["secret"] = "hmac-key" } });

        await harness.RunAsync();

        var request = Assert.Single(harness.ApiRequests);
        Assert.Equal("/orders?page=1", request.RequestUri!.PathAndQuery);
        Assert.Equal(Assert.Single(harness.Signatures.Signed), request.Headers.GetValues("X-Signature").Single());
    }

    [Fact]
    public async Task A_signer_that_sends_the_request_elsewhere_abandons_the_job_and_nothing_leaves()
    {
        await using var harness = Harness(
            """{ "type": "signed", "credential": "example-shop", "signer": "moving" }""",
            configure: http => http.Signer<MovingSigner>("moving"));
        harness.Vault.Add("shop-7", "example-shop", new Credential { Token = "tok" });

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync());

        Assert.StartsWith(HttpErrorCodes.SignerMovedRequest, thrown.Message);
        Assert.Empty(harness.ApiRequests);
    }

    [Fact]
    public void An_open_recipe_needs_no_credential_store()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSyncLaneHttp(http => http.Recipe("orders", Recipe(null)).WriteTo((_, _) => Task.CompletedTask));

        Assert.DoesNotContain(services, service => service.ServiceType == typeof(ICredentialStore));
    }

    internal static string Recipe(string? auth, string? pagination = null) =>
        $$"""
        {
          "name": "test/orders",
          "request": { "url": "https://api.example.com/orders" },
          "records": "$.items"
          {{(pagination is null ? "" : $", \"pagination\": {pagination}")}}
          {{(auth is null ? "" : $", \"auth\": {auth}")}}
        }
        """;

    private static AuthHarness Harness(
        string auth, Func<HttpRequestMessage, HttpResponseMessage>? api = null, string? pagination = null, Action<HttpSourceOptions>? configure = null) =>
        new(Recipe(auth, pagination), api ?? (_ => AuthHarness.Ok()), configure: configure);

    private static string? Authorization(HttpRequestMessage request) =>
        request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null;
}
