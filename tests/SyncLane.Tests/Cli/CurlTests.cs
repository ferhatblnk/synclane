using System.Text.Json;
using System.Text.Json.Serialization;
using SyncLane.Cli;
using SyncLane.Cli.Curl;
using SyncLane.Http;

namespace SyncLane.Tests.Cli;

/// <summary>
/// Curls as people copy them: from API documentation, from a browser, for bash or for Windows. Every token here is made
/// up, and none may reach the recipe, its notes or the text written to disk.
/// </summary>
public class CurlTests
{
    [Fact]
    public void A_documentation_curl_over_several_lines_becomes_a_request_with_its_token_moved_to_auth()
    {
        var draft = Draft("""
            curl -L \
              -H "Accept: application/vnd.github+json" \
              -H "Authorization: Bearer ghp_MADEUPTOKEN123" \
              -H "X-GitHub-Api-Version: 2022-11-28" \
              "https://api.github.com/repos/dotnet/runtime/issues?state=all&per_page=100"
            """);

        Assert.Equal("github/issues", draft.Recipe.Name);
        Assert.Equal(("GET", "https://api.github.com/repos/dotnet/runtime/issues"), (draft.Recipe.Request.Method, draft.Recipe.Request.Url));
        Assert.Equal(new Dictionary<string, string> { ["state"] = "all", ["per_page"] = "100" }, draft.Recipe.Request.Query);
        Assert.Equal(["Accept", "X-GitHub-Api-Version"], draft.Recipe.Request.Headers.Keys.Order());
        Assert.Equal((AuthType.Bearer, "github"), (draft.Recipe.Auth!.Type, draft.Recipe.Auth.Credential));
        Assert.Equal("ghp_MADEUPTOKEN123", draft.Secret!.Value);
        Assert.NoSecretIn(draft, "ghp_MADEUPTOKEN123");
    }

    [Fact]
    public void A_user_and_data_with_get_become_basic_auth_and_query_parameters()
    {
        var draft = Draft("curl https://api.stripe.com/v1/customers -u sk_test_MADEUPKEY: -d limit=3 -G");

        Assert.Equal(("GET", "https://api.stripe.com/v1/customers"), (draft.Recipe.Request.Method, draft.Recipe.Request.Url));
        Assert.Equal("3", draft.Recipe.Request.Query["limit"]);
        Assert.Equal(AuthType.Basic, draft.Recipe.Auth!.Type);
        Assert.Equal("sk_test_MADEUPKEY:", draft.Secret!.Value);
        Assert.NoSecretIn(draft, "sk_test_MADEUPKEY");
    }

    [Theory]
    [InlineData("X-Shopify-Access-Token", "shpat_MADEUP")]
    [InlineData("x-api-key", "MADEUP-api-key")]
    [InlineData("Ocp-Apim-Subscription-Key", "MADEUPsubscription")]
    [InlineData("PRIVATE-TOKEN", "glpat-MADEUP")]
    public void A_header_that_carries_a_key_becomes_an_api_key_in_that_header(string header, string value)
    {
        var draft = Draft($"""curl -X GET "https://shop.example.com/admin/api/2025-01/orders.json?status=any" -H "{header}: {value}" """);

        Assert.Equal((AuthType.ApiKey, ApiKeyLocation.Header, header), (draft.Recipe.Auth!.Type, draft.Recipe.Auth.In, draft.Recipe.Auth.Name));
        Assert.Equal("orders", draft.Recipe.Name.Split('/')[1]);
        Assert.NoSecretIn(draft, value);
    }

    [Fact]
    public void A_key_in_the_query_becomes_an_api_key_in_the_query_and_leaves_the_rest()
    {
        var draft = Draft("curl 'https://api.example.com/v1/items?api_key=MADEUPKEY42&page=1&updated_since=2026-10-01'");

        Assert.Equal((ApiKeyLocation.Query, "api_key"), (draft.Recipe.Auth!.In, draft.Recipe.Auth.Name));
        Assert.Equal(["page", "updated_since"], draft.Recipe.Request.Query.Keys.Order());
        Assert.NoSecretIn(draft, "MADEUPKEY42");
    }

    [Fact]
    public void A_browser_copy_for_bash_loses_its_noise_and_keeps_its_json_body()
    {
        var draft = Draft("""
            curl 'https://api.example.com/graph/search' \
              -H 'accept: application/json' \
              -H 'accept-language: tr-TR,tr;q=0.9' \
              -H 'authorization: Bearer MADEUP.JWT.TOKEN' \
              -H 'content-type: application/json' \
              -H 'cookie: session=MADEUPSESSION' \
              -H 'origin: https://app.example.com' \
              -H 'sec-ch-ua: "Chromium";v="141"' \
              -H 'sec-fetch-mode: cors' \
              -H 'user-agent: Mozilla/5.0' \
              --data-raw $'{"query":"it\'s","size":100}'
            """);

        Assert.Equal("POST", draft.Recipe.Request.Method);
        Assert.Equal(["accept"], draft.Recipe.Request.Headers.Keys);
        Assert.Equal("""{"query":"it's","size":100}""", draft.Recipe.Request.Body!.Value.GetRawText());
        Assert.Contains(draft.Notes, note => note.StartsWith("Browser headers left out: accept-language, cookie, origin, sec-ch-ua, sec-fetch-mode, user-agent", StringComparison.Ordinal));
        Assert.NoSecretIn(draft, "MADEUP.JWT.TOKEN");
        Assert.NoSecretIn(draft, "MADEUPSESSION");
    }

    [Fact]
    public void A_browser_copy_for_windows_cmd_is_read_with_its_carets()
    {
        var draft = Draft("""
            curl ^"https://api.example.com/v1/orders?from=2026-10-01^&size=50^" ^
              -H ^"accept: application/json^" ^
              -H ^"x-api-key: MADEUPWINKEY^" ^
              --compressed
            """);

        Assert.Equal("https://api.example.com/v1/orders", draft.Recipe.Request.Url);
        Assert.Equal(new Dictionary<string, string> { ["from"] = "2026-10-01", ["size"] = "50" }, draft.Recipe.Request.Query);
        Assert.Equal("x-api-key", draft.Recipe.Auth!.Name);
        Assert.Contains(draft.Notes, note => note.Contains("--compressed", StringComparison.Ordinal));
        Assert.NoSecretIn(draft, "MADEUPWINKEY");
    }

    [Fact]
    public void Json_shorthand_and_another_authorization_scheme_are_kept_as_they_mean()
    {
        var draft = Draft("""curl --json '{"page":1}' -H "Authorization: Token MADEUPLEGACY" https://api.example.com/search""");

        Assert.Equal(("POST", "{\"page\":1}"), (draft.Recipe.Request.Method, draft.Recipe.Request.Body!.Value.GetRawText()));
        Assert.Equal((AuthType.ApiKey, "Authorization", "Token "), (draft.Recipe.Auth!.Type, draft.Recipe.Auth.Name, draft.Recipe.Auth.Prefix));
        Assert.Equal(["Accept"], draft.Recipe.Request.Headers.Keys);
        Assert.NoSecretIn(draft, "MADEUPLEGACY");
    }

    [Fact]
    public void A_request_with_two_credentials_keeps_one_and_says_a_signer_is_needed()
    {
        var draft = Draft("""curl https://sellingpartnerapi-eu.amazon.com/orders/v0/orders -H "x-amz-access-token: Atza|MADEUP" -H "x-amz-security-token: MADEUPSTS" """);

        Assert.Equal("amazon/orders", draft.Recipe.Name);
        Assert.Contains(draft.Notes, note => note.Contains("IRequestSigner", StringComparison.Ordinal));
        Assert.NoSecretIn(draft, "Atza|MADEUP");
        Assert.NoSecretIn(draft, "MADEUPSTS");
    }

    [Fact]
    public void A_command_without_a_url_is_refused_with_how_to_paste_it()
    {
        var error = Assert.Single(CurlCommand.Parse("curl -H 'Accept: application/json'").Errors);

        Assert.Equal(CliErrorCodes.CurlWithoutUrl, error.Code);
    }

    [Theory]
    [InlineData("api.example-shop.com", "example-shop")]
    [InlineData("api.example-shop.com.tr", "example-shop")]
    [InlineData("seller.example.co.uk", "example")]
    [InlineData("example.io", "example")]
    [InlineData("127.0.0.1", "api")]
    [InlineData("[::1]", "api")]
    [InlineData("localhost", "localhost")]
    public void The_credential_is_named_after_the_site_not_its_country_suffix_or_an_address(string host, string name)
    {
        Assert.Equal(name, RecipeDraft.CredentialName(new Uri($"https://{host}/v1/orders").Host));
    }

    private static RecipeDraft Draft(string command)
    {
        var curl = CurlCommand.Parse(command);
        Assert.True(curl.Succeeded, string.Join('\n', curl.Errors));

        var draft = RecipeDraft.From(curl.Value!);
        Assert.True(draft.Recipe.Validate().Succeeded, string.Join('\n', draft.Recipe.Validate().Errors));
        return draft;
    }
}

/// <summary>The one check every curl test ends with.</summary>
file static class Assert
{
    private static readonly JsonSerializerOptions Written = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>The secret appears nowhere a user or a file could see it, and the draft written out reads back as a recipe.</summary>
    public static void NoSecretIn(RecipeDraft draft, string secret)
    {
        var written = JsonSerializer.Serialize(draft.Recipe, Written);
        Xunit.Assert.DoesNotContain(secret, written);
        Xunit.Assert.All(draft.Notes, note => Xunit.Assert.DoesNotContain(secret, note));
        Xunit.Assert.DoesNotContain(secret, draft.Secret?.ToString() ?? "");
        Xunit.Assert.True(HttpRecipe.Parse(written).Succeeded, string.Join('\n', HttpRecipe.Parse(written).Errors));
    }

    public static void Equal<T>(T expected, T actual) => Xunit.Assert.Equal(expected, actual);

    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Xunit.Assert.Equal(expected, actual);

    public static void True(bool condition, string message) => Xunit.Assert.True(condition, message);

    public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter) => Xunit.Assert.Contains(collection, filter);

    public static T Single<T>(IEnumerable<T> collection) => Xunit.Assert.Single(collection);
}
