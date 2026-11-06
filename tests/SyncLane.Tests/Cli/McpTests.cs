using System.IO.Pipelines;
using System.Net;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SyncLane.Cli;
using SyncLane.Cli.Mcp;
using SyncLane.Tests.Support;

namespace SyncLane.Tests.Cli;

/// <summary>
/// <c>synclane mcp</c> as an assistant sees it: over a real MCP session with a client, against a fake shop API. The
/// tools return what the commands print and nothing more: no token, wherever it came from.
/// </summary>
public sealed class McpTests : IDisposable
{
    private const string Url = "https://api.example-shop.com/v1/orders?updated_since=2026-10-01T00:00:00Z&page=1&per_page=2";
    private const string EnvironmentToken = "ENVTOKEN";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("synclane-mcp-");
    private readonly ShopApi _api = new();

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public async Task An_assistant_sees_every_tool_with_its_description_and_whether_it_reaches_the_network()
    {
        await using var session = await Session.StartAsync(Host());

        var tools = await session.Client.ListToolsAsync();

        Assert.Equal(
            ["synclane_check", "synclane_explain_error", "synclane_init", "synclane_recipe_guide", "synclane_recipe_schema", "synclane_test"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
        Assert.Equal(
            ["synclane_init", "synclane_test"],
            tools.Where(tool => tool.ProtocolTool.Annotations?.OpenWorldHint == true).Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Contains("environment variable", session.Client.ServerInstructions);
    }

    [Fact]
    public async Task Init_reads_a_token_written_as_a_variable_from_the_environment_and_never_returns_it()
    {
        await using var session = await Session.StartAsync(Host());

        var init = await CallAsync(session, "synclane_init", new() { ["curl"] = $"curl -H 'Authorization: Bearer $SHOP_TOKEN' '{Url}'" });
        var test = await CallAsync(session, "synclane_test", new() { ["file"] = "orders.jsonc" });

        Assert.Equal($"Bearer {EnvironmentToken}", _api.Seen[0].Authorization);
        Assert.StartsWith("Done.", init);
        Assert.Contains("--- orders.jsonc ---", init);
        Assert.Contains("\"name\"", init);
        Assert.StartsWith("Done.", test);
        Assert.DoesNotContain(EnvironmentToken, init);
        Assert.DoesNotContain(EnvironmentToken, test);
        Assert.DoesNotContain(EnvironmentToken, await File.ReadAllTextAsync(Path.Combine(_folder.FullName, "orders.jsonc")));
    }

    [Fact]
    public async Task A_path_outside_the_folder_is_refused_before_anything_is_sent_or_read()
    {
        var tools = new RecipeTools(Host());
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.jsonc");
        var escaped = $"escaped-{Guid.NewGuid():N}.jsonc";

        var init = await tools.InitAsync($"curl '{Url}'", file: $"../{escaped}");
        var check = await tools.CheckAsync("../../etc/hosts");
        var test = await tools.TestAsync(outside);

        Assert.All([init, check, test], text => Assert.Contains(CliErrorCodes.PathOutsideFolder, text));
        Assert.Empty(_api.Seen);
        Assert.False(File.Exists(Path.Combine(_folder.Parent!.FullName, escaped)));
    }

    [Fact]
    public async Task Check_names_every_problem_of_a_recipe_and_says_when_it_is_valid()
    {
        var tools = new RecipeTools(Host());
        await File.WriteAllTextAsync(Path.Combine(_folder.FullName, "broken.jsonc"), """{ "name": "shop/orders" }""");
        await tools.InitAsync($"curl '{Url}'");

        var broken = await tools.CheckAsync("broken.jsonc");
        var valid = await tools.CheckAsync("orders.jsonc");

        Assert.Contains("problems", broken);
        Assert.Contains("SL5", broken);
        Assert.Contains("is a valid recipe", valid);
    }

    [Fact]
    public void The_guide_and_the_error_catalog_are_the_ones_in_docs()
    {
        var tools = new RecipeTools(Host());
        var docs = Path.Combine(Repository(), "docs");

        Assert.Equal(File.ReadAllText(Path.Combine(docs, "recipes.md")), tools.Guide());
        Assert.Contains("never planned", tools.ExplainError(SyncErrorCodes.OwnerTypeNotPlanned));
        Assert.Contains("Fix:", tools.ExplainError(" sl6012 "));
        Assert.Contains("not a code this version knows", tools.ExplainError("SL9999"));
        Assert.Contains("not a SyncLane error code", tools.ExplainError("drop table"));
        Assert.Contains("\"$schema\"", tools.Schema());
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_stops_the_server_before_it_starts()
    {
        using var error = new StringWriter();
        var host = new CliHost { Out = TextWriter.Null, Error = error, Directory = _folder.FullName };

        var code = await McpCommand.RunAsync(host, "no-such-folder", CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains(CliErrorCodes.FolderMissing, error.ToString());
    }

    [Fact]
    public void A_variable_that_is_not_set_is_left_as_written()
    {
        var tools = new RecipeTools(Host());

        Assert.Equal(
            $"-H 'X-Key: {EnvironmentToken}' -H 'X-Other: $UNSET' {EnvironmentToken}x",
            tools.Expand("-H 'X-Key: $SHOP_TOKEN' -H 'X-Other: $UNSET' ${SHOP_TOKEN}x"));
    }

    private static async Task<string> CallAsync(Session session, string tool, Dictionary<string, object?> arguments)
    {
        var result = await session.Client.CallToolAsync(tool, arguments);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    private CliHost Host() => new()
    {
        Out = TextWriter.Null,
        Error = TextWriter.Null,
        Directory = _folder.FullName,
        Environment = name => name is "SHOP_TOKEN" or "SYNCLANE_EXAMPLE_SHOP_TOKEN" ? EnvironmentToken : null,
        Network = () => _api,
        Time = new ManualClock(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero))
    };

    private static string Repository()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "CLAUDE.md")))
            folder = folder.Parent;

        return folder?.FullName ?? throw new DirectoryNotFoundException(AppContext.BaseDirectory);
    }

    /// <summary>A server and a client joined by two pipes, as an assistant and <c>synclane mcp</c> are by stdio.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly McpServer _server;
        private readonly Task _running;
        private readonly CancellationTokenSource _stop = new();

        private Session(McpServer server, McpClient client)
        {
            _server = server;
            Client = client;
            _running = server.RunAsync(_stop.Token);
        }

        public McpClient Client { get; private set; }

        public static async Task<Session> StartAsync(CliHost host)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var options = RecipeTools.Options(host);
            var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
            var session = new Session(server, null!);
            session.Client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            await _running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _server.DisposeAsync();
            _stop.Dispose();
        }
    }

    /// <summary>Two pages of two orders each, then nothing.</summary>
    private sealed class ShopApi : HttpMessageHandler
    {
        public List<(string? Page, string? Authorization)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = request.RequestUri!.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=')).FirstOrDefault(pair => pair[0] == "page")?[1];
            lock (Seen)
                Seen.Add((page, request.Headers.Authorization?.ToString()));

            var number = int.Parse(page ?? "1", System.Globalization.CultureInfo.InvariantCulture);
            var ids = number switch { 1 => new[] { 1, 2 }, 2 => [3, 4], _ => [] };
            var records = string.Join(", ", ids.Select(id => $$"""{ "id": {{id}}, "updated_at": "2026-10-30T09:00:00Z", "total": {{id * 10}} }"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "data": [{{records}}] }""", Encoding.UTF8, "application/json")
            });
        }
    }
}
