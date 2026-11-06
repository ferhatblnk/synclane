using System.CommandLine;
using System.CommandLine.Help;
using SyncLane.Cli.Commands;
using SyncLane.Cli.Mcp;

namespace SyncLane.Cli;

/// <summary>The <c>synclane</c> command: <c>init</c>, <c>test</c> and <c>mcp</c>, with help and the version from the parser.</summary>
internal static class SynclaneCommand
{
    public static Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);

        // Not a RootCommand: that takes its name from the assembly, SyncLane.Cli, and people type synclane.
        var root = new Command("synclane", "Turns a working curl into a SyncLane recipe, and tests a recipe without a database.")
        {
            new HelpOption(),
            new VersionOption(),
            InitCommand.Create(host),
            TestCommand.Create(host),
            McpCommand.Create(host)
        };

        // Response files are off: in a curl, a word that starts with @ is curl's own (-d @body.json), not ours.
        var parsed = root.Parse(Separate(args), new ParserConfiguration { ResponseFileTokenReplacer = null });
        return parsed.InvokeAsync(new InvocationConfiguration { Output = host.Out, Error = host.Error }, cancellationToken);
    }

    /// <summary>
    /// A curl pasted after <c>synclane init</c> brings its own options (<c>-H</c>, <c>-d</c>, <c>-u</c>). A <c>--</c>
    /// before its first word tells the parser that everything from there on is the curl's.
    /// </summary>
    internal static string[] Separate(string[] args)
    {
        if (args.Length < 2 || args[0] != "init")
            return args;

        var curl = Array.IndexOf(args, "curl", 1);
        if (curl < 0 || Array.IndexOf(args, "--", 1, curl - 1) >= 0)
            return args;

        return [.. args[..curl], "--", .. args[curl..]];
    }
}
