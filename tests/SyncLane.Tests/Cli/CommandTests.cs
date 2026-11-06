using System.Net;
using System.Text;
using SyncLane.Cli;
using SyncLane.Cli.Commands;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Cli;

/// <summary>
/// The synclane command end to end, against a fake shop API: a curl pasted after <c>synclane init</c> becomes a recipe
/// file, and <c>synclane test</c> runs that file through the real handler. No database, no real network.
/// </summary>
public sealed class CommandTests : IDisposable
{
    private const string Curl = "https://api.example-shop.com/v1/orders?updated_since=2026-10-01T00:00:00Z&page=1&per_page=2";
    private const string Token = "MADEUPTOKEN";
    private const string EnvironmentToken = "ENVTOKEN";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("synclane-cli-");
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        _folder.Delete(recursive: true);
        _out.Dispose();
        _error.Dispose();
    }

    [Fact]
    public async Task A_curl_pasted_after_init_becomes_a_recipe_that_test_runs_for_two_pages()
    {
        var api = new ShopApi();

        var init = await Run(api, null, "init", "curl", "-H", $"Authorization: Bearer {Token}", "-H", "Accept: application/json", Curl);

        Assert.Equal(0, init);
        Assert.Contains("Wrote orders.jsonc.", _out.ToString());
        Assert.Contains("Next: synclane test orders.jsonc", _out.ToString());
        Assert.Equal($"Bearer {Token}", api.Seen.Single().Authorization);

        var test = await Run(api, null, "test", "orders.jsonc");

        var output = _out.ToString();
        Assert.Equal(0, test);
        Assert.Contains("Page 1       2 records", output);
        Assert.Contains("Page 2       2 records", output);
        Assert.Contains("Fields:  id, updated_at, total", output);
        Assert.Contains("Key:     $.id, unique across 4 records.", output);
        Assert.Contains("Paging:  page 2 brought new records; more pages follow.", output);
        Assert.Contains("Budget:  'example-shop' was not used", output);
        Assert.Contains("The recipe works.", output);
        Assert.All(api.Seen.Skip(1), request => Assert.Equal($"Bearer {EnvironmentToken}", request.Authorization));
        Assert.Equal(["1", "2"], api.Seen.Skip(1).Select(request => request.Page));
        NothingSecretIn(output + _error);
        NothingSecretIn(File.ReadAllText(Path.Combine(_folder.FullName, "orders.jsonc")));
    }

    [Fact]
    public async Task Options_before_the_curl_are_the_commands_and_the_ones_after_it_are_the_curls()
    {
        var exit = await Run(new ShopApi(), null, "init", "--out", "shop.jsonc", "curl", "-H", $"Authorization: Bearer {Token}", "-o", "ignored.json", Curl);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "shop.jsonc")));
        Assert.Contains("Options not used: -o.", _out.ToString());
    }

    [Fact]
    public async Task A_piped_curl_works_like_a_pasted_one_and_keeps_the_token_out_of_the_shell_history()
    {
        var piped = $"""
            curl '{Curl}' \
              -H 'Authorization: Bearer {Token}'
            """;

        var exit = await Run(new ShopApi(), new StringReader(piped), "init");

        Assert.Equal(0, exit);
        Assert.Contains("Wrote orders.jsonc.", _out.ToString());
        NothingSecretIn(_out.ToString() + _error);
    }

    [Fact]
    public async Task Init_does_not_replace_a_file_unless_told_to()
    {
        File.WriteAllText(Path.Combine(_folder.FullName, "orders.jsonc"), "mine");

        var refused = await Run(new ShopApi(), null, "init", "curl", Curl);
        var forced = await Run(new ShopApi(), null, "init", "--force", "curl", Curl);

        Assert.Equal((1, 0), (refused, forced));
        Assert.Contains(CliErrorCodes.OutputExists, _error.ToString());
        Assert.NotEqual("mine", File.ReadAllText(Path.Combine(_folder.FullName, "orders.jsonc")));
    }

    [Fact]
    public async Task A_paging_parameter_the_API_ignores_fails_the_test_and_names_the_parameter()
    {
        await Run(new ShopApi(), null, "init", "curl", Curl);

        var exit = await Run(new ShopApi { IgnoresPage = true }, null, "test", "orders.jsonc");

        Assert.Equal(1, exit);
        Assert.Contains("Paging:  page 2 repeated page 1.", _out.ToString());
        Assert.Contains("Key:     $.id, unique within each page.", _out.ToString());
        Assert.Contains($"{CliErrorCodes.PageRepeated}: Page 2 brought the same records as page 1: the API did not read the page parameter 'page'.", _error.ToString());
        Assert.DoesNotContain("The recipe works.", _out.ToString());
    }

    [Fact]
    public async Task Without_a_terminal_a_missing_credential_names_the_variables_to_set()
    {
        await Run(new ShopApi(), null, "init", "curl", "-H", $"Authorization: Bearer {Token}", Curl);

        var exit = await SynclaneCommand.RunAsync(["test", "orders.jsonc"], Host(new ShopApi(), environment: _ => null), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains($"{CliErrorCodes.CredentialNotGiven}: The recipe needs the credential 'example-shop'", _error.ToString());
        Assert.Contains("SYNCLANE_EXAMPLE_SHOP_TOKEN", _error.ToString());
    }

    [Fact]
    public async Task In_a_terminal_the_credential_is_asked_for_and_never_shown()
    {
        await Run(new ShopApi(), null, "init", "curl", "-H", $"Authorization: Bearer {Token}", Curl);
        var api = new ShopApi();
        var asked = new List<string>();

        var exit = await SynclaneCommand.RunAsync(["test", "orders.jsonc"],
            Host(api, environment: _ => null, ask: prompt => { asked.Add(prompt); return "TYPEDTOKEN"; }), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(["example-shop token: "], asked);
        Assert.All(api.Seen, request => Assert.Equal("Bearer TYPEDTOKEN", request.Authorization));
        Assert.DoesNotContain("TYPEDTOKEN", _out.ToString() + _error);
    }

    [Fact]
    public async Task A_page_the_API_refuses_ends_the_test_with_the_catalogs_message()
    {
        await Run(new ShopApi(), null, "init", "curl", "-H", $"Authorization: Bearer {Token}", Curl);

        var exit = await Run(new ShopApi { Refuses = true }, null, "test", "orders.jsonc");

        Assert.Equal(1, exit);
        Assert.Contains("Page 1 failed.", _out.ToString());
        Assert.Contains("SL5", _error.ToString());
        NothingSecretIn(_out.ToString() + _error);
    }

    [Fact]
    public async Task A_recipe_that_uses_the_owner_asks_for_one()
    {
        File.WriteAllText(Path.Combine(_folder.FullName, "owned.json"), """
            { "name": "shop/orders", "request": { "url": "https://api.example-shop.com/v1/{{owner.key}}/orders" }, "records": "$.data" }
            """);

        var without = await Run(new ShopApi(), null, "test", "owned.json");
        var with = await Run(new ShopApi(), null, "test", "owned.json", "--owner", "store-1");

        Assert.Equal((1, 0), (without, with));
        Assert.Contains(CliErrorCodes.OwnerNeeded, _error.ToString());
    }

    [Fact]
    public async Task A_missing_file_and_a_bad_since_are_explained()
    {
        await Run(new ShopApi(), null, "init", "curl", Curl);

        Assert.Equal(1, await Run(new ShopApi(), null, "test", "nowhere.jsonc"));
        Assert.Equal(1, await Run(new ShopApi(), null, "test", "orders.jsonc", "--since", "last week"));
        Assert.Contains(CliErrorCodes.RecipeFileMissing, _error.ToString());
        Assert.Contains(CliErrorCodes.SinceInvalid, _error.ToString());
    }

    [Fact]
    public async Task A_redirect_and_an_unreachable_host_are_explained_without_the_url()
    {
        var redirect = await Run(new ShopApi { Redirects = true }, null, "init", "curl", Curl);
        var unreachable = await Run(new ShopApi { Unreachable = true }, null, "init", "curl", Curl);

        Assert.Equal((1, 1), (redirect, unreachable));
        Assert.Contains("recipes do not follow redirects", _error.ToString());
        Assert.Contains($"{CliErrorCodes.ProbeUnreachable}: The curl's request did not reach the API: the host name could not be resolved.", _error.ToString());
        Assert.DoesNotContain("example-shop.com", _error.ToString());
    }

    [Fact]
    public async Task Help_names_the_command_as_people_type_it()
    {
        await Run(new ShopApi(), null, "--help");

        Assert.Contains("synclane [command] [options]", _out.ToString());
        Assert.Contains("init <curl>", _out.ToString());
        Assert.Contains("test <recipe>", _out.ToString());
    }

    [Theory]
    [InlineData("7d", 7 * 24)]
    [InlineData("12h", 12)]
    [InlineData("2026-10-30", 24)]
    public void Since_takes_a_length_of_time_or_a_date(string since, int hours)
    {
        var now = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddHours(-hours), TestCommand.Start(since, now));
    }

    [Fact]
    public void Only_the_words_from_curl_on_are_kept_from_the_parser()
    {
        Assert.Equal(["init", "-o", "x.jsonc", "--", "curl", "-H", "A: b", "https://x.example"], SynclaneCommand.Separate(["init", "-o", "x.jsonc", "curl", "-H", "A: b", "https://x.example"]));
        Assert.Equal(["test", "curl"], SynclaneCommand.Separate(["test", "curl"]));
        Assert.Equal(["init", "--", "curl"], SynclaneCommand.Separate(["init", "--", "curl"]));
    }

    [Fact]
    public async Task A_recipe_that_uses_the_owner_s_values_is_tested_with_them_and_names_the_ones_missing()
    {
        var api = new ShopApi();
        await Run(api, null, "init", "curl", "-H", $"Authorization: Bearer {Token}", Curl);
        var path = Path.Combine(_folder.FullName, "orders.jsonc");
        var recipe = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, recipe.Replace("\"query\": {", "\"query\": {\n    \"region\": \"{{owner.region}}\",", StringComparison.Ordinal));
        _error.GetStringBuilder().Clear();

        var without = await Run(api, null, "test", "orders.jsonc");
        var missing = _error.ToString();
        var with = await Run(api, null, "test", "orders.jsonc", "--value", "region=eu");

        Assert.Equal((1, 0), (without, with));
        Assert.Contains(CliErrorCodes.OwnerValuesNeeded, missing);
        Assert.Contains("--value region=...", missing);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "x=y" }, TestCommand.Pairs(["a=1", "b=x=y"]));
    }

    private Task<int> Run(ShopApi api, TextReader? piped, params string[] args) =>
        SynclaneCommand.RunAsync(args, Host(api, piped), CancellationToken.None);

    private CliHost Host(ShopApi api, TextReader? piped = null, Func<string, string?>? environment = null, Func<string, string?>? ask = null) => new()
    {
        Out = _out,
        Error = _error,
        Directory = _folder.FullName,
        Piped = piped,
        AskSecret = ask,
        Environment = environment ?? (name => name == "SYNCLANE_EXAMPLE_SHOP_TOKEN" ? EnvironmentToken : null),
        Network = () => api,
        Time = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero))
    };

    private static void NothingSecretIn(string text)
    {
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(EnvironmentToken, text);
    }

    /// <summary>Two pages of two orders each, then nothing; with switches for the ways an API goes wrong.</summary>
    private sealed class ShopApi : HttpMessageHandler
    {
        public bool IgnoresPage { get; init; }

        public bool Refuses { get; init; }

        public bool Redirects { get; init; }

        public bool Unreachable { get; init; }

        public List<(string? Page, string? Authorization)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No such host: {request.RequestUri}");

            var page = request.RequestUri!.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=')).FirstOrDefault(pair => pair[0] == "page")?[1];
            Seen.Add((page, request.Headers.Authorization?.ToString()));

            if (Redirects)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://api.example-shop.com/v2/orders") } });

            if (Refuses)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var number = IgnoresPage ? 1 : int.Parse(page ?? "1", System.Globalization.CultureInfo.InvariantCulture);
            var ids = number switch { 1 => new[] { 1, 2 }, 2 => [3, 4], _ => [] };
            var records = string.Join(", ", ids.Select(id => $$"""{ "id": {{id}}, "updated_at": "2026-10-30T09:00:00Z", "total": {{id * 10}} }"""));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "data": [{{records}}] }""", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-ratelimit-remaining", "99");
            response.Headers.Add("x-ratelimit-reset", "30");
            return Task.FromResult(response);
        }
    }
}
