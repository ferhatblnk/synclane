using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;
using SyncLane.Tests.Http.Support;

namespace SyncLane.Tests.Http;

public class PagingTests
{
    [Fact]
    public async Task Page_numbers_count_up_and_a_short_page_is_the_last()
    {
        const string pagination = """{ "type": "page", "param": "page", "size": 2, "sizeParam": "per_page" }""";
        var api = FakeApi.Json("""{ "items": [1, 2] }""");

        var first = await RunAsync(api, pagination, cursor: null);
        var last = await RunAsync(FakeApi.Json("""{ "items": [3] }"""), pagination, cursor: "2");

        Assert.Equal(new JobPage("2", 2), first);
        Assert.Equal(new JobPage(null, 1), last);
        Assert.Equal("https://api.example.com/items?page=1&per_page=2", Assert.Single(api.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Without_a_size_the_first_empty_page_is_the_last()
    {
        const string pagination = """{ "type": "page", "param": "p", "start": 0 }""";

        Assert.Equal(new JobPage("1", 1), await RunAsync(FakeApi.Json("""{ "items": [1] }"""), pagination, cursor: null));
        Assert.Equal(new JobPage(null, 0), await RunAsync(FakeApi.Json("""{ "items": [] }"""), pagination, cursor: "1"));
    }

    [Fact]
    public async Task Offsets_move_by_the_records_read()
    {
        const string pagination = """{ "type": "offset", "param": "offset", "size": 3, "sizeParam": "limit" }""";
        var api = FakeApi.Json("""{ "items": [1, 2, 3] }""");

        var first = await RunAsync(api, pagination, cursor: null);
        var last = await RunAsync(FakeApi.Json("""{ "items": [4] }"""), pagination, cursor: "3");

        Assert.Equal(new JobPage("3", 3), first);
        Assert.Equal(new JobPage(null, 1), last);
        Assert.Equal("https://api.example.com/items?offset=0&limit=3", Assert.Single(api.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_cursor_from_the_answer_is_sent_back_until_there_is_none()
    {
        const string pagination = """{ "type": "cursor", "param": "cursor", "from": "$.meta.next" }""";
        var api = FakeApi.Json("""{ "items": [1], "meta": { "next": "abc==" } }""");

        var first = await RunAsync(api, pagination, cursor: null);
        var second = await RunAsync(FakeApi.Json("""{ "items": [2], "meta": { "next": 12345 } }"""), pagination, cursor: "abc==");
        var last = await RunAsync(FakeApi.Json("""{ "items": [3], "meta": { "next": null } }"""), pagination, cursor: "12345");

        Assert.Equal(new JobPage("abc==", 1), first);
        Assert.Equal(new JobPage("12345", 1), second);
        Assert.Equal(new JobPage(null, 1), last);
        Assert.Equal("https://api.example.com/items", Assert.Single(api.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_cursor_can_come_from_the_last_record_and_stop_on_a_has_more_flag()
    {
        const string pagination = """{ "type": "cursor", "param": "starting_after", "fromLastRecord": "id", "hasMore": "$.has_more" }""";
        var api = FakeApi.Json("""{ "items": [{ "id": "ord_1" }, { "id": "ord_2" }], "has_more": true }""");

        var first = await RunAsync(api, pagination, cursor: "ord_0");
        var last = await RunAsync(FakeApi.Json("""{ "items": [{ "id": "ord_3" }], "has_more": false }"""), pagination, cursor: "ord_2");

        Assert.Equal(new JobPage("ord_2", 2), first);
        Assert.Equal(new JobPage(null, 1), last);
        Assert.Equal("https://api.example.com/items?starting_after=ord_0", Assert.Single(api.Requests).RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{ "type": "cursor", "param": "c", "from": "$.next" }""", null, """{ "items": [1], "next": { "a": 1 } }""", HttpErrorCodes.CursorValueInvalid)]
    [InlineData("""{ "type": "cursor", "param": "c", "from": "$.next", "hasMore": "$.more" }""", null, """{ "items": [1], "next": "x", "more": "yes" }""", HttpErrorCodes.CursorValueInvalid)]
    [InlineData("""{ "type": "cursor", "param": "c", "from": "$.next" }""", "same", """{ "items": [1], "next": "same" }""", HttpErrorCodes.PageRepeated)]
    [InlineData("""{ "type": "nextUrl", "from": "$.next" }""", null, """{ "items": [1], "next": "https://api.example.com/items" }""", HttpErrorCodes.PageRepeated)]
    [InlineData("""{ "type": "nextUrl", "from": "$.next" }""", null, """{ "items": [1], "next": "https://other.example.net/items?page=2" }""", HttpErrorCodes.NextUrlForeignHost)]
    [InlineData("""{ "type": "nextUrl", "from": "$.next" }""", null, """{ "items": [1], "next": "http://api.example.com/items?page=2" }""", HttpErrorCodes.NextUrlForeignHost)]
    [InlineData("""{ "type": "nextUrl", "from": "$.next" }""", null, """{ "items": [1], "next": "https://api.example.com:8443/items" }""", HttpErrorCodes.NextUrlForeignHost)]
    public async Task A_next_page_that_cannot_be_followed_safely_abandons_before_anything_is_written(string pagination, string? cursor, string body, string code)
    {
        var written = 0;

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() =>
            RunAsync(FakeApi.Json(body), pagination, cursor, sink: (_, _) =>
            {
                written++;
                return Task.CompletedTask;
            }));

        Assert.StartsWith(code, thrown.Message);
        Assert.Equal(0, written);
        Assert.DoesNotContain("other.example.net", thrown.Message);
    }

    [Fact]
    public async Task A_next_url_may_be_relative_and_is_called_as_written()
    {
        const string pagination = """{ "type": "nextUrl", "from": "$.links.next" }""";
        var next = await RunAsync(FakeApi.Json("""{ "items": [1], "links": { "next": "/items?page=2&token=a%2Fb" } }"""), pagination, cursor: null);
        var api = FakeApi.Json("""{ "items": [2], "links": { "next": null } }""");

        var last = await RunAsync(api, pagination, cursor: next.NextCursor);

        Assert.Equal("https://api.example.com/items?page=2&token=a%2Fb", next.NextCursor);
        Assert.Equal(new JobPage(null, 1), last);
        var request = Assert.Single(api.Requests);
        Assert.Equal("https://api.example.com/items?page=2&token=a%2Fb", request.RequestUri!.AbsoluteUri);
        Assert.Equal("yes", Assert.Single(request.Headers.GetValues("X-Kept")));
    }

    [Fact]
    public async Task The_link_header_gives_the_next_page()
    {
        var api = new FakeApi((_, _) =>
        {
            var response = FakeApi.Response(HttpStatusCode.OK, """{ "items": [1] }""");
            response.Headers.TryAddWithoutValidation("Link",
                """<https://api.example.com/items?page=1>; rel="prev first", <https://api.example.com/items?page=3>; rel="next", <https://api.example.com/items?page=9>; rel="last" """);
            return Task.FromResult(response);
        });

        var page = await RunAsync(api, """{ "type": "linkHeader" }""", cursor: null);
        var last = await RunAsync(FakeApi.Json("""{ "items": [2] }"""), """{ "type": "linkHeader" }""", cursor: "https://api.example.com/items?page=3");

        Assert.Equal(new JobPage("https://api.example.com/items?page=3", 1), page);
        Assert.Equal(new JobPage(null, 1), last);
    }

    [Theory]
    [InlineData("""{ "type": "page", "param": "page" }""", "4", HttpErrorCodes.TooManyPages)]
    [InlineData("""{ "type": "offset", "param": "offset", "size": 10 }""", "30", HttpErrorCodes.TooManyPages)]
    [InlineData("""{ "type": "page", "param": "page" }""", "abc", HttpErrorCodes.CursorUnreadable)]
    [InlineData("""{ "type": "page", "param": "page" }""", "0", HttpErrorCodes.CursorUnreadable)]
    [InlineData("""{ "type": "nextUrl", "from": "$.next" }""", "https://other.example.net/items", HttpErrorCodes.NextUrlForeignHost)]
    [InlineData("""{ "type": "nextUrl", "from": "$.next" }""", "not a url", HttpErrorCodes.CursorUnreadable)]
    public async Task A_cursor_that_must_not_be_followed_abandons_before_anything_is_sent(string pagination, string cursor, string code)
    {
        var api = FakeApi.Json("""{ "items": [] }""");

        var thrown = await Assert.ThrowsAsync<JobAbandonedException>(() => RunAsync(api, pagination, cursor, maxPages: 3));

        Assert.StartsWith(code, thrown.Message);
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("""{ "type": "page" }""", "param")]
    [InlineData("""{ "type": "offset", "param": "offset" }""", "size")]
    [InlineData("""{ "type": "page", "param": "page", "from": "$.next" }""", "from")]
    [InlineData("""{ "type": "cursor", "param": "c" }""", "from")]
    [InlineData("""{ "type": "cursor", "param": "c", "from": "$.a", "fromLastRecord": "id" }""", "from")]
    [InlineData("""{ "type": "cursor", "param": "c", "from": "$.a", "start": 1 }""", "start")]
    [InlineData("""{ "type": "nextUrl" }""", "from")]
    [InlineData("""{ "type": "linkHeader", "param": "page" }""", "param")]
    [InlineData("""{ "type": "page", "param": "page", "sizeParam": "per_page" }""", "sizeParam")]
    [InlineData("""{ "type": "page", "param": "page", "size": 0 }""", "size")]
    [InlineData("""{ "type": "page", "param": "page", "start": -1 }""", "start")]
    [InlineData("""{ "type": "cursor", "param": "c", "from": "next" }""", "from")]
    [InlineData("""{ "type": "cursor", "param": "c", "fromLastRecord": "a.b" }""", "fromLastRecord")]
    public void A_pagination_that_cannot_work_is_refused_with_the_field_named(string pagination, string field)
    {
        var result = HttpRecipe.Parse(Recipe(pagination));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == HttpErrorCodes.PaginationInvalid && error.Message.Contains($"pagination.{field} "));
    }

    [Fact]
    public void A_paging_parameter_cannot_also_be_set_in_the_query()
    {
        var result = HttpRecipe.Parse(Recipe("""{ "type": "page", "param": "fixed" }""", query: """{ "fixed": "1" }"""));

        Assert.Contains("pagination.param 'fixed' is also set in request.query", result.Error!.Message);
    }

    [Fact]
    public void An_unknown_pagination_type_is_refused()
    {
        Assert.Equal(HttpErrorCodes.RecipeJsonInvalid, HttpRecipe.Parse(Recipe("""{ "type": "token" }""")).Error!.Code);
    }

    private static string Recipe(string pagination, string query = "{}") =>
        $$"""
        {
          "name": "test/items",
          "request": { "url": "https://api.example.com/items", "query": {{query}}, "headers": { "X-Kept": "yes" } },
          "records": "$.items",
          "pagination": {{pagination}}
        }
        """;

    private static async Task<JobPage> RunAsync(
        FakeApi api, string pagination, string? cursor, int maxPages = 1000, Func<RecordPage, CancellationToken, Task>? sink = null)
    {
        var services = new ServiceCollection();
        services.AddSyncLaneHttp(http =>
        {
            http.MaxPagesPerJob = maxPages;
            http.Recipe("items", Recipe(pagination)).WriteTo(sink ?? ((_, _) => Task.CompletedTask));
        });
        services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var job = new Job
        {
            Id = 1,
            Queue = "shops",
            Owner = "shop-7",
            Lane = "shop-7",
            Type = "items",
            Mode = JobMode.Initial,
            Status = JobStatus.Running,
            Cursor = cursor
        };

        return await scope.ServiceProvider.GetRequiredService<HttpRecipeHandler>().RunPageAsync(job, CancellationToken.None);
    }
}
