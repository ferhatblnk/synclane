using System.CommandLine;
using ModelContextProtocol.Server;

namespace SyncLane.Cli.Mcp;

/// <summary>
/// <c>synclane mcp</c>: serves the recipe tools to an AI assistant over standard input and output. The assistant starts
/// it, on the person's machine, with the person's own subscription; SyncLane calls no AI service and opens no port.
/// </summary>
internal static class McpCommand
{
    public static Command Create(CliHost host)
    {
        var folder = new Option<string?>("--folder")
        {
            Description = "The folder the tools read and write recipes in. By default, the folder the assistant starts the server in."
        };
        var command = new Command("mcp", "Serves the recipe tools to an AI assistant (MCP over standard input and output). Add it to the assistant's MCP settings; it is not meant to be typed.")
        {
            folder
        };
        command.SetAction((result, cancellationToken) => RunAsync(host, result.GetValue(folder), cancellationToken));
        return command;
    }

    internal static async Task<int> RunAsync(CliHost host, string? folder, CancellationToken cancellationToken)
    {
        if (folder is not null && !Directory.Exists(Path.GetFullPath(folder, host.Directory)))
            return host.Fail(CliErrors.FolderMissing(folder));

        var options = RecipeTools.Options(folder is null ? host : host.In(Path.GetFullPath(folder, host.Directory)));
        await using var transport = new StdioServerTransport(options, loggerFactory: null);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken);
        return 0;
    }
}
