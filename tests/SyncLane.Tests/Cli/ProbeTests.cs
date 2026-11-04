using System.Net;
using System.Text;
using System.Text.Json;
using SyncLane.Cli;
using SyncLane.Cli.Curl;
using SyncLane.Cli.Probe;
using SyncLane.Http;

namespace SyncLane.Tests.Cli;

/// <summary>
/// One answer, and what it says about a recipe. The answers are the shapes of the live check's APIs, cut short; the
/// gate is that the probe writes what a person wrote by hand in samples/LiveCheck/recipes.
/// </summary>
public class ProbeTests
{
    [Fact]
    public void Page_numbers_in_the_curl_and_an_answer_that_is_the_array_give_the_breweries_recipe()
    {
        var probed = Probe("curl 'https://api.openbrewerydb.org/v1/breweries?by_state=oregon&per_page=50&page=1'",
            """[{ "id": "b-1", "name": "A" }, { "id": "b-2", "name": "B" }]""");

        Assert.Equal(LiveCheck("breweries").Pagination, probed.Recipe.Pagination);
        Assert.Equal(LiveCheck("breweries").Records, probed.Recipe.Records);
        Assert.Equal(LiveCheck("breweries").Request.Query, probed.Recipe.Request.Query);
    }

    [Fact]
    public void A_cursor_in_the_answer_gives_the_author_feed_recipe_except_the_owner_the_curl_cannot_know()
    {
        var probed = Probe("curl 'https://public.api.bsky.app/xrpc/app.bsky.feed.getAuthorFeed?actor=someone.example&limit=100'",
            """{ "feed": [{ "post": { "uri": "at://a" } }, { "post": { "uri": "at://b" } }], "cursor": "2026-10-30T10:00:00.000Z" }""");

        Assert.Equal(LiveCheck("author-feed").Pagination, probed.Recipe.Pagination);
        Assert.Equal("$.feed", probed.Recipe.Records);
        Assert.Equal(["actor"], probed.Recipe.Request.Query.Keys);
    }

    [Fact]
    public void A_link_header_and_a_date_in_the_query_give_the_issues_recipe_and_the_rate_headers_come_with_it()
    {
        var probed = Probe("""
            curl -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" \
              "https://api.github.com/repos/dotnet/runtime/issues?state=all&since=2026-10-01T00:00:00Z&per_page=100"
            """,
            """[{ "id": 1, "number": 7, "updated_at": "2026-10-30T09:00:00Z" }]""",
            ("Link", "<https://api.github.com/repositories/1/issues?since=2026-10-01T00%3A00%3A00Z&page=2>; rel=\"next\""),
            ("x-ratelimit-limit", "60"), ("x-ratelimit-remaining", "59"), ("x-ratelimit-reset", "1793361600"));

        var hand = LiveCheck("issues");
        Assert.Equal((hand.Records, hand.Pagination), (probed.Recipe.Records, probed.Recipe.Pagination));
        Assert.Equal(hand.Request.Query, probed.Recipe.Request.Query);
        Assert.Equal(hand.Request.Headers, probed.Recipe.Request.Headers);
        Assert.Equal(("x-ratelimit-remaining", "x-ratelimit-reset", RateResetFormat.Unix), (probed.Recipe.Rate!.Headers!.Remaining, probed.Recipe.Rate.Headers.Reset, probed.Recipe.Rate.Headers.ResetFormat));
        Assert.Contains(probed.Notes, note => note.Contains("VersionedBy(\"$.updated_at\")", StringComparison.Ordinal));
        Assert.Contains(probed.Notes, note => note.Contains("x-ratelimit-limit: 60", StringComparison.Ordinal));
    }

    [Fact]
    public void An_offset_and_a_start_and_end_time_give_the_earthquakes_recipe()
    {
        var probed = Probe("curl 'https://earthquake.usgs.gov/fdsnws/event/1/query?format=geojson&starttime=2026-10-01T00:00:00&endtime=2026-10-02T00:00:00&orderby=time-asc&limit=100&offset=1'",
            """{ "type": "FeatureCollection", "metadata": { "generated": 1793361600000, "count": 2 }, "features": [{ "type": "Feature", "id": "us1" }, { "type": "Feature", "id": "us2" }] }""");

        var hand = LiveCheck("earthquakes");
        Assert.Equal((hand.Records, hand.Pagination), (probed.Recipe.Records, probed.Recipe.Pagination));
        Assert.Equal(hand.Request.Query, probed.Recipe.Request.Query);
    }

    [Fact]
    public void A_next_url_in_the_answer_gives_the_pokemon_recipe()
    {
        var probed = Probe("curl 'https://pokeapi.co/api/v2/pokemon?limit=200'",
            """{ "count": 1302, "next": "https://pokeapi.co/api/v2/pokemon?offset=200&limit=200", "previous": null, "results": [{ "name": "a", "url": "https://pokeapi.co/api/v2/pokemon/1/" }] }""");

        var hand = LiveCheck("pokemon");
        Assert.Equal((hand.Records, hand.Pagination), (probed.Recipe.Records, probed.Recipe.Pagination));
        Assert.Equal(hand.Request.Query, probed.Recipe.Request.Query);
    }

    [Fact]
    public void More_pages_without_a_cursor_continue_after_the_last_records_id()
    {
        var probed = Probe("curl https://api.example.com/v1/customers -u key_MADEUP: -d limit=3 -G",
            """{ "object": "list", "data": [{ "id": "c1" }, { "id": "c2" }, { "id": "c3" }], "has_more": true }""");

        Assert.Equal(new HttpRecipePagination { Type = PaginationType.Cursor, Param = "starting_after", FromLastRecord = "id", HasMore = "$.has_more", Size = 3, SizeParam = "limit" }, probed.Recipe.Pagination);
        Assert.Contains("Check the parameter's name", probed.Comments["pagination"]);
    }

    [Theory]
    [InlineData("X-Rate-Limit-Remaining", "39", "X-Rate-Limit-Reset", "41", RateRemainingFormat.Number, RateResetFormat.Seconds)]
    [InlineData("RateLimit-Remaining", "10", "RateLimit-Reset", "1793361600123", RateRemainingFormat.Number, RateResetFormat.UnixMilliseconds)]
    [InlineData("RateLimit", "\"default\";r=50;t=30", null, null, RateRemainingFormat.Ietf, null)]
    [InlineData("x-current-token-count", "4", null, null, RateRemainingFormat.Number, null)]
    [InlineData("X-Shop-Api-Call-Limit", "32/40", null, null, RateRemainingFormat.UsedOfLimit, null)]
    public void The_rate_headers_the_answer_carries_become_the_rate_section(
        string remaining, string value, string? reset, string? resetValue, RateRemainingFormat format, RateResetFormat? resetFormat)
    {
        var headers = new List<(string, string)> { (remaining, value) };
        if (reset is not null)
            headers.Add((reset, resetValue!));

        var probed = Probe("curl https://api.example-shop.com/v1/orders", """[{ "id": 1 }]""", [.. headers]);

        var rate = probed.Recipe.Rate!;
        Assert.Equal(("example-shop", remaining, format, reset, resetFormat), (rate.Budget, rate.Headers!.Remaining, rate.Headers.RemainingFormat ?? RateRemainingFormat.Number, rate.Headers.Reset, rate.Headers.ResetFormat));
        Assert.Contains(probed.Notes, note => note.Contains("lane.AddBudget(\"example-shop\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_policy_field_becomes_the_limit_header_since_it_says_its_own_window()
    {
        var probed = Probe("curl https://api.example-shop.com/v1/orders", """[{ "id": 1 }]""",
            ("RateLimit", "\"default\";r=50;t=30"), ("RateLimit-Policy", "\"default\";q=100;w=60"));

        var headers = probed.Recipe.Rate!.Headers!;
        Assert.Equal(("RateLimit", "RateLimit-Policy", RateLimitFormat.Ietf, (int?)null), (headers.Remaining, headers.Limit, headers.LimitFormat, headers.LimitWindow));
        Assert.Contains("the limit in RateLimit-Policy", probed.Comments["rate"]);
    }

    [Theory]
    [InlineData("X-Rate-Limit-Limit")]
    [InlineData("x-vendor-RateLimit-Limit")]
    public void A_bare_limit_number_is_left_out_with_a_note_since_its_window_is_only_in_the_documentation(string name)
    {
        var probed = Probe("curl https://api.example-shop.com/v1/orders", """[{ "id": 1 }]""", ("X-Rate-Limit-Remaining", "39"), (name, "40"));

        Assert.Null(probed.Recipe.Rate!.Headers!.Limit);
        Assert.Contains(probed.Notes, note => note.Contains($"\"limit\": \"{name}\"", StringComparison.Ordinal) && note.Contains("limitWindow", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("from=2026-10-01&to=2026-10-02", WindowFormat.Date)]
    [InlineData("created_after=1790000000&created_before=1790086400", WindowFormat.Unix)]
    public void Dates_in_the_query_become_the_window_in_their_format(string query, WindowFormat format)
    {
        var probed = Probe($"curl 'https://api.example.com/v1/orders?{query}'", """[{ "id": 1 }]""");

        Assert.Equal(format, probed.Recipe.Window!.Format);
        Assert.Equal(["{{window.end}}", "{{window.start}}"], probed.Recipe.Request.Query.Values.Order());
    }

    [Fact]
    public void Records_without_a_unique_id_are_reported_with_the_fields_that_could_key_them()
    {
        var probed = Probe("curl https://api.example.com/v1/lines", """{ "items": [{ "sku": "a", "qty": 1 }, { "sku": "b", "qty": 1 }] }""");

        Assert.Contains(probed.Notes, note => note.Contains("$.sku", StringComparison.Ordinal) && note.Contains("KeyedBy", StringComparison.Ordinal));
    }

    [Fact]
    public void The_written_recipe_explains_its_guesses_reads_back_as_a_recipe_and_holds_no_secret()
    {
        var probed = Probe("""curl -H "Authorization: Bearer MADEUPTOKEN" "https://api.example.com/v1/orders?updated_since=2026-10-01T00:00:00Z&page=1&per_page=50" """,
            """{ "data": [{ "id": 1, "updated_at": "2026-10-30T09:00:00Z" }, { "id": 2, "updated_at": "2026-10-30T09:00:00Z" }] }""");

        var text = RecipeText.Write(probed.Recipe, probed.Comments, probed.Notes);

        Assert.Contains("// guessed: the array of 2 objects at $.data\n  \"records\": \"$.data\"", text);
        Assert.Contains("// guessed: '2026-10-01T00:00:00Z' looks like the start of a time range", text);
        Assert.Contains("\"pagination\": { \"type\": \"page\", \"param\": \"page\", \"size\": 50, \"sizeParam\": \"per_page\" }", text);
        Assert.DoesNotContain("MADEUPTOKEN", text);
        var parsed = HttpRecipe.Parse(text);
        Assert.True(parsed.Succeeded, string.Join('\n', parsed.Errors));
    }

    [Fact]
    public void The_written_recipe_keeps_plus_signs_ampersands_and_accents_as_people_typed_them()
    {
        var probed = Probe("""curl -H "Accept: application/vnd.example+json" "https://api.example.com/v1/şehirler?q=a%26b%20ç" """, """[{ "id": 1 }]""");

        var text = RecipeText.Write(probed.Recipe, probed.Comments, probed.Notes);

        Assert.Contains("\"Accept\": \"application/vnd.example+json\"", text);
        Assert.Contains("\"q\": \"a&b ç\"", text);
        Assert.DoesNotContain("\\u", text);
        Assert.Equal("a&b ç", HttpRecipe.Parse(text).Value!.Request.Query["q"]);
    }

    [Fact]
    public async Task The_probe_sends_the_curls_credential_once_and_reads_the_answer()
    {
        var api = new Answering(_ => Json("""{ "items": [{ "id": 1 }] }"""));
        var draft = Draft("""curl "https://api.example.com/v1/orders?api_key=MADEUPKEY&limit=10" """);

        var probed = await Prober.ProbeAsync(draft, new HttpClient(api), CancellationToken.None);

        Assert.True(probed.Succeeded);
        Assert.Equal("https://api.example.com/v1/orders?limit=10&api_key=MADEUPKEY", api.Requests.Single());
        Assert.DoesNotContain("MADEUPKEY", RecipeText.Write(probed.Value!.Recipe, probed.Value.Comments, probed.Value.Notes));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, "SL6002", "copy a fresh curl")]
    [InlineData(HttpStatusCode.OK, "<html>sign in</html>", "SL6003", "not JSON")]
    public async Task An_answer_that_cannot_teach_anything_is_refused_with_why(HttpStatusCode status, string? body, string code, string hint)
    {
        var api = new Answering(_ => new HttpResponseMessage(status) { Content = new StringContent(body ?? "", Encoding.UTF8, "text/html") });

        var probed = await Prober.ProbeAsync(Draft("curl https://api.example.com/v1/orders"), new HttpClient(api), CancellationToken.None);

        var error = Assert.Single(probed.Errors);
        Assert.Equal(code, error.Code);
        Assert.Contains(hint, error.Message);
    }

    private static Inferred Probe(string curl, string answer, params (string Name, string Value)[] headers)
    {
        using var document = JsonDocument.Parse(answer);
        return Inference.Infer(Draft(curl).Recipe, document.RootElement.Clone(), headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static RecipeDraft Draft(string curl) => RecipeDraft.From(CurlCommand.Parse(curl).Value!);

    private static HttpRecipe LiveCheck(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "SyncLane.slnx")))
            directory = directory.Parent;

        return HttpRecipe.Parse(File.ReadAllText(Path.Combine(directory.FullName, "samples", "LiveCheck", "recipes", $"{name}.json"))).Value!;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }
}
