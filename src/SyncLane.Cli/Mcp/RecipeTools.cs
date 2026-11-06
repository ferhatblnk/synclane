using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SyncLane.Cli.Commands;
using SyncLane.Http;

namespace SyncLane.Cli.Mcp;

/// <summary>
/// The tools <c>synclane mcp</c> gives an AI assistant. Each runs the same code as the command of its name, with the
/// terminal replaced by text the assistant reads, so what the commands never print (records, cursors, URLs with keys,
/// credentials) the tools never return. Files are read and written only inside the folder the server started in.
/// </summary>
internal sealed partial class RecipeTools(CliHost host)
{
    public const string Instructions =
        """
        SyncLane recipes describe, in JSON, how to read one API resource page by page into a database. To write one:
        1. Read synclane_recipe_guide once; synclane_recipe_schema has every field.
        2. Ask the person for a curl command that works, then call synclane_init with it. It sends that request once and
           writes a draft with a comment beside every field it guessed.
        3. Fix what its notes say, and check the file with synclane_check.
        4. Run synclane_test: it fetches two pages and reports the records' fields, whether the key is unique and whether
           paging works. It reports structure, never data.
        Credentials never go in a recipe or in this conversation. If the curl needs a token or key, ask the person to put
        it in an environment variable of this server and write $NAME in the curl instead; synclane_init reads it there.
        synclane_test names the variables a recipe's credential needs. An error code such as SL5106 is explained by
        synclane_explain_error.
        """;

    public static McpServerOptions Options(CliHost host)
    {
        var tools = new RecipeTools(host);
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "synclane", Version = Version() },
            ServerInstructions = Instructions,
            ToolCollection =
            [
                McpServerTool.Create(tools.InitAsync, new()
                {
                    Name = "synclane_init",
                    Description = "Turns a working curl into a recipe file: sends the request once, to the URL in the curl, and writes a " +
                                  "draft with a comment beside every guessed field. Returns what it wrote. $NAME in the curl is read " +
                                  "from this server's environment, so a token never has to appear in the conversation.",
                    OpenWorld = true,
                    Destructive = false
                }),
                McpServerTool.Create(tools.TestAsync, new()
                {
                    Name = "synclane_test",
                    Description = "Runs a recipe for two pages against the real API, without a database, and reports its records' " +
                                  "fields, whether the key is unique and whether page 2 brought new records. Structure only, never data.",
                    OpenWorld = true,
                    ReadOnly = true
                }),
                McpServerTool.Create(tools.CheckAsync, new()
                {
                    Name = "synclane_check",
                    Description = "Checks a recipe file without sending anything: every problem, with how to fix it.",
                    ReadOnly = true,
                    OpenWorld = false
                }),
                McpServerTool.Create(tools.Schema, new()
                {
                    Name = "synclane_recipe_schema",
                    Description = "The JSON Schema of a recipe, with a description of every field and value.",
                    ReadOnly = true,
                    OpenWorld = false
                }),
                McpServerTool.Create(tools.Guide, new()
                {
                    Name = "synclane_recipe_guide",
                    Description = "The guide to writing recipes: paging styles, windows, records, keys, authentication, rate limits, examples.",
                    ReadOnly = true,
                    OpenWorld = false
                }),
                McpServerTool.Create(tools.ExplainError, new()
                {
                    Name = "synclane_explain_error",
                    Description = "What a SyncLane error code (such as SL5106) means, and how to fix it.",
                    ReadOnly = true,
                    OpenWorld = false
                })
            ]
        };
    }

    public async Task<string> InitAsync(
        [Description("The curl command, as copied from a browser's developer tools or an API's documentation. Write $NAME where a secret goes.")]
        string curl,
        [Description("The file to write, inside the folder. By default the resource's name with .jsonc.")]
        string? file = null,
        [Description("Replace the file if it exists.")]
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(curl);

        string? written = null;
        var text = await CaptureAsync(Expand(curl), async captured =>
        {
            var (code, path) = await InitCommand.WriteAsync([], file, force, captured, cancellationToken, Outside);
            written = path;
            return code;
        });

        return written is null ? text : $"{text}\n--- {Path.GetFileName(written)} ---\n{await File.ReadAllTextAsync(written, cancellationToken)}";
    }

    public Task<string> TestAsync(
        [Description("The recipe file, inside the folder.")]
        string file,
        [Description("The owner key to test with, for a recipe that uses {{owner.*}}.")]
        string? owner = null,
        [Description("How far back the window reaches: a length such as 7d, 12h or 30m, or a date.")]
        string since = "7d",
        [Description("The owner's values, for a recipe that uses {{owner.name}}, such as { \"region\": \"eu\" }. Not secrets.")]
        Dictionary<string, string>? values = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Outside(file) is { } outside
            ? Task.FromResult(Failed(outside))
            : CaptureAsync(null, captured => TestCommand.RunAsync(file, owner, since, captured, cancellationToken, values));
    }

    public async Task<string> CheckAsync(
        [Description("The recipe file, inside the folder.")]
        string file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (Outside(file) is { } outside)
            return Failed(outside);

        var path = Path.GetFullPath(file, host.Directory);
        if (!File.Exists(path))
            return Failed(CliErrors.RecipeFileMissing(file));

        var parsed = HttpRecipe.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return parsed.Succeeded
            ? $"{file} is a valid recipe for {parsed.Value!.Name}. Next: synclane_test."
            : $"{file} has {parsed.Errors.Count} problems:\n{string.Join('\n', parsed.Errors)}";
    }

    public string Schema() => HttpRecipe.JsonSchema;

    public string Guide() => Embedded("recipes.md");

    public string ExplainError(
        [Description("The code, such as SL5106.")]
        string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var wanted = code.Trim().ToUpperInvariant();
        if (!ErrorCode().IsMatch(wanted))
            return $"'{code}' is not a SyncLane error code; codes look like SL5106.";

        var row = Embedded("errors.md").Split('\n').FirstOrDefault(line => line.StartsWith($"| `{wanted}` |", StringComparison.Ordinal));
        if (row is null)
            return $"{wanted} is not a code this version knows. Newer versions add codes; see docs/errors.md in the repository.";

        var cells = row.Split('|', StringSplitOptions.TrimEntries);
        return $"{wanted} ({cells[2]})\nCause: {cells[3]}\nFix: {cells[4]}";
    }

    /// <summary>Replaces <c>$NAME</c> and <c>${NAME}</c> with the variable's value, where it is set; leaves the rest.</summary>
    internal string Expand(string curl) =>
        Variable().Replace(curl, match => host.Environment(match.Groups["name"].Value) is { Length: > 0 } value ? value : match.Value);

    /// <summary>The refusal for a path outside the folder, or <c>null</c> for one inside it.</summary>
    internal SyncError? Outside(string file)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(host.Directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file, host.Directory).StartsWith(folder, StringComparison.Ordinal) ? null : CliErrors.PathOutsideFolder(file);
    }

    /// <summary>Runs a command with its terminal replaced by text, and returns that text, first line saying whether it worked.</summary>
    private async Task<string> CaptureAsync(string? piped, Func<CliHost, Task<int>> run)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var captured = new CliHost
        {
            Out = output,
            Error = error,
            Directory = host.Directory,
            Piped = piped is null ? null : new StringReader(piped),
            AskSecret = null,
            Environment = host.Environment,
            Network = host.Network,
            Time = host.Time
        };

        var code = await run(captured);
        var text = new StringBuilder(code == 0 ? "Done.\n" : "Failed.\n").Append(output).Append(error);
        return text.ToString().TrimEnd();
    }

    private static string Failed(SyncError error) => $"Failed.\n{error}";

    private static string Embedded(string name)
    {
        using var stream = typeof(RecipeTools).Assembly.GetManifestResourceStream($"SyncLane.Cli.{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Version() =>
        typeof(RecipeTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    [GeneratedRegex(@"\$\{?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}?")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^SL\d{4}$")]
    private static partial Regex ErrorCode();
}
