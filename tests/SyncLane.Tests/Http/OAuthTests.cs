using System.Net;
using System.Text;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Http;

public class OAuthTests
{
    private const string ClientCredentials =
        $$"""{ "type": "oauth2", "credential": "example-shop", "grant": "clientCredentials", "tokenUrl": "{{AuthHarness.TokenUrl}}", "scope": "orders:read" }""";

    private const string RefreshToken =
        $$"""{ "type": "oauth2", "credential": "example-shop", "grant": "refreshToken", "tokenUrl": "{{AuthHarness.TokenUrl}}" }""";

    private static readonly DateTimeOffset Noon = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_client_credentials_token_is_fetched_once_and_kept_for_the_next_pages()
    {
        await using var harness = Harness(ClientCredentials, _ => AuthHarness.Token("token-1", expiresIn: 3600));
        harness.Vault.Add("shop-7", "example-shop", Client("id 1", "secret:1"));

        await harness.RunAsync();
        await harness.RunAsync();

        var call = Assert.Single(harness.TokenCalls);
        Assert.Equal(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "orders:read" }, call.Form);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("id+1:secret%3A1")), call.Authorization);
        Assert.All(harness.ApiRequests, request => Assert.Equal("Bearer token-1", Authorization(request)));
    }

    [Fact]
    public async Task Client_authentication_in_the_body_sends_the_id_and_secret_as_form_fields()
    {
        await using var harness = Harness(ClientCredentials.Replace("\"scope\"", "\"clientAuth\": \"body\", \"scope\"", StringComparison.Ordinal));
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        await harness.RunAsync();

        var call = Assert.Single(harness.TokenCalls);
        Assert.Null(call.Authorization);
        Assert.Equal(("id-1", "secret-1"), (call.Form["client_id"], call.Form["client_secret"]));
    }

    [Theory]
    [InlineData("""{ "access_token": "token-1", "token_type": "bearer", "expires_in": 600 }""")]
    [InlineData("""{ "access_token": "token-1", "token_type": "Bearer", "expires_in": "600" }""")]
    public async Task A_token_is_renewed_shortly_before_it_expires(string answer)
    {
        var clock = new ManualClock(Noon);
        var issued = 0;
        await using var harness = Harness(
            ClientCredentials,
            _ => FakeApi.Response(HttpStatusCode.OK, answer.Replace("token-1", $"token-{++issued}", StringComparison.Ordinal)),
            clock);
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        await harness.RunAsync();
        clock.Advance(TimeSpan.FromSeconds(539));
        await harness.RunAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await harness.RunAsync();

        Assert.Equal(["Bearer token-1", "Bearer token-1", "Bearer token-2"], harness.ApiRequests.Select(Authorization));
    }

    [Fact]
    public async Task A_kept_token_the_api_rejects_is_renewed_once_and_the_page_sent_again()
    {
        var issued = 0;
        var revoked = false;
        await using var harness = Harness(
            ClientCredentials,
            _ => AuthHarness.Token($"token-{++issued}"),
            api: request => revoked && Authorization(request) == "Bearer token-1"
                ? FakeApi.Response(HttpStatusCode.Unauthorized)
                : AuthHarness.Ok());
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        await harness.RunAsync();
        revoked = true;
        var page = await harness.RunAsync();

        Assert.Equal(new JobPage(null, 0), page);
        Assert.Equal(["Bearer token-1", "Bearer token-1", "Bearer token-2"], harness.ApiRequests.Select(Authorization));
        Assert.Equal(2, harness.TokenCalls.Count);
    }

    [Fact]
    public async Task A_fresh_token_the_api_rejects_abandons_the_job_without_asking_again()
    {
        await using var harness = Harness(ClientCredentials, api: _ => FakeApi.Response(HttpStatusCode.Unauthorized));
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync());

        Assert.StartsWith(HttpErrorCodes.Unauthorized, thrown.Message);
        Assert.Single(harness.ApiRequests);
        Assert.Single(harness.TokenCalls);
    }

    [Fact]
    public async Task A_rotated_refresh_token_is_saved_before_the_page_goes_on()
    {
        await using var harness = Harness(RefreshToken, _ => AuthHarness.Token("token-1", 3600, refreshToken: "refresh-2"));
        harness.Vault.Add("shop-7", "example-shop", new Credential { ClientId = "id-1", RefreshToken = "refresh-1" });

        await harness.RunAsync();
        await harness.RunAsync();

        var call = Assert.Single(harness.TokenCalls);
        Assert.Equal(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "refresh-1", ["client_id"] = "id-1" }, call.Form);
        Assert.Equal((new CredentialKey("shop-7", "example-shop"), "refresh-2"), Assert.Single(harness.Vault.Saved));
        Assert.Equal("refresh-2", harness.Vault.Credentials[new CredentialKey("shop-7", "example-shop")].RefreshToken);
    }

    [Fact]
    public async Task A_refresh_token_another_instance_just_rotated_is_read_again_from_the_store()
    {
        CredentialVault? vault = null;
        await using var harness = Harness(RefreshToken, call =>
        {
            if (call.Form["refresh_token"] == "refresh-2")
                return AuthHarness.Token("token-2", 3600);

            // Another instance used refresh-1 a moment ago and saved its successor.
            vault!.Add("shop-7", "example-shop", new Credential { ClientId = "id-1", RefreshToken = "refresh-2" });
            return FakeApi.Response(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }""");
        });
        vault = harness.Vault.Add("shop-7", "example-shop", new Credential { ClientId = "id-1", RefreshToken = "refresh-1" });

        await harness.RunAsync();

        Assert.Equal(["refresh-1", "refresh-2"], harness.TokenCalls.Select(call => call.Form["refresh_token"]));
        Assert.Equal("Bearer token-2", Authorization(Assert.Single(harness.ApiRequests)));
    }

    [Fact]
    public async Task A_refused_token_abandons_the_job_with_the_standard_error_code_and_nothing_else_from_the_answer()
    {
        await using var harness = Harness(RefreshToken, _ =>
            FakeApi.Response(HttpStatusCode.BadRequest, """{ "error": "invalid_grant", "error_description": "token secret-detail was revoked" }"""));
        harness.Vault.Add("shop-7", "example-shop", new Credential { ClientId = "id-1", RefreshToken = "refresh-1" });

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync());

        Assert.StartsWith(HttpErrorCodes.TokenRefused, thrown.Message);
        Assert.Contains("400 (invalid_grant)", thrown.Message);
        Assert.DoesNotContain("secret-detail", thrown.Message);
        Assert.Empty(harness.ApiRequests);
    }

    [Fact]
    public async Task An_error_code_outside_the_standard_is_not_quoted()
    {
        await using var harness = Harness(ClientCredentials, _ => FakeApi.Response(HttpStatusCode.Unauthorized, """{ "error": "secret-detail" }"""));
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync());

        Assert.Contains("refused to issue a token with 401.", thrown.Message);
        Assert.DoesNotContain("secret-detail", thrown.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, typeof(SyncLaneException), "SL5122")]
    [InlineData(HttpStatusCode.RequestTimeout, typeof(SyncLaneException), "SL5122")]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(JobThrottledException), "SL5122")]
    [InlineData(HttpStatusCode.Forbidden, typeof(JobAbandonedException), "SL5121")]
    [InlineData(HttpStatusCode.Found, typeof(JobAbandonedException), "SL5121")]
    public async Task A_token_endpoint_failure_retries_waits_or_abandons_as_its_status_says(HttpStatusCode status, Type outcome, string code)
    {
        await using var harness = Harness(ClientCredentials, _ =>
        {
            var response = FakeApi.Response(status, """{ "message": "secret-detail" }""");
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return response;
        });
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => harness.RunAsync());

        Assert.IsType(outcome, thrown);
        Assert.StartsWith(code, thrown.Message);
        Assert.DoesNotContain("secret-detail", thrown.Message);
        Assert.Empty(harness.ApiRequests);
        if (thrown is JobThrottledException throttled)
            Assert.Equal(TimeSpan.FromSeconds(30), throttled.RetryAfter);
    }

    [Fact]
    public async Task An_unreachable_token_endpoint_is_retried_and_its_exception_message_is_not_kept()
    {
        await using var harness = Harness(ClientCredentials, _ =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host: https://auth.example.com/?secret-detail"));
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        var thrown = await Assert.ThrowsAsync<SyncLaneException>(() => harness.RunAsync());

        Assert.Equal(HttpErrorCodes.TokenUnavailable, thrown.Error.Code);
        Assert.Contains("the host name could not be resolved", thrown.Message);
        Assert.Null(thrown.InnerException);
    }

    [Theory]
    [InlineData("""{ "token_type": "bearer" }""", "has no access_token")]
    [InlineData("""{ "access_token": "" }""", "has no access_token")]
    [InlineData("""{ "access_token": "token-1", "token_type": "mac" }""", "has a token_type other than bearer")]
    [InlineData("""{ "access_token": "token 1" }""", "has an access_token with characters a header cannot carry")]
    [InlineData("""<html>secret-detail</html>""", "is not JSON")]
    public async Task A_token_answer_that_cannot_be_used_abandons_the_job(string answer, string expected)
    {
        await using var harness = Harness(ClientCredentials, _ => FakeApi.Response(HttpStatusCode.OK, answer));
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync());

        Assert.StartsWith(HttpErrorCodes.TokenAnswerInvalid, thrown.Message);
        Assert.Contains(expected, thrown.Message);
        Assert.DoesNotContain("secret-detail", thrown.Message);
    }

    [Fact]
    public async Task A_client_credentials_grant_without_a_secret_abandons_the_job_before_calling_the_endpoint()
    {
        await using var harness = Harness(ClientCredentials);
        harness.Vault.Add("shop-7", "example-shop", new Credential { ClientId = "id-1" });

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => harness.RunAsync());

        Assert.StartsWith(HttpErrorCodes.CredentialInvalid, thrown.Message);
        Assert.Contains("has no ClientSecret", thrown.Message);
        Assert.Empty(harness.TokenCalls);
    }

    [Fact]
    public async Task Pages_that_need_a_token_at_the_same_moment_share_one_request()
    {
        await using var harness = Harness(ClientCredentials, _ => AuthHarness.Token("token-1", 3600), tokenDelay: TimeSpan.FromMilliseconds(200));
        harness.Vault.Add("shop-7", "example-shop", Client("id-1", "secret-1"));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => harness.RunAsync()));

        Assert.Single(harness.TokenCalls);
        Assert.Equal(8, harness.ApiRequests.Count);
    }

    [Fact]
    public async Task Owners_never_share_a_token_and_a_changed_credential_gets_a_new_one()
    {
        var issued = 0;
        await using var harness = Harness(ClientCredentials, _ => AuthHarness.Token($"token-{++issued}", 3600));
        harness.Vault.Add("shop-7", "example-shop", Client("id-7", "secret-7")).Add("shop-8", "example-shop", Client("id-8", "secret-8"));

        await harness.RunAsync("shop-7");
        await harness.RunAsync("shop-8");
        harness.Vault.Add("shop-7", "example-shop", Client("id-7", "secret-7b"));
        await harness.RunAsync("shop-7");

        Assert.Equal(["Bearer token-1", "Bearer token-2", "Bearer token-3"], harness.ApiRequests.Select(Authorization));
    }

    private static Credential Client(string id, string secret) => new() { ClientId = id, ClientSecret = secret };

    private static AuthHarness Harness(
        string auth,
        Func<TokenCall, HttpResponseMessage>? tokens = null,
        TimeProvider? time = null,
        Func<HttpRequestMessage, HttpResponseMessage>? api = null,
        TimeSpan? tokenDelay = null) =>
        new(AuthTests.Recipe(auth), api ?? (_ => AuthHarness.Ok()), tokens, time, tokenDelay: tokenDelay);

    private static string? Authorization(HttpRequestMessage request) =>
        request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null;
}
