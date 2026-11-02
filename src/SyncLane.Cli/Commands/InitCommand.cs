using System.CommandLine;
using SyncLane.Cli.Curl;
using SyncLane.Cli.Probe;

namespace SyncLane.Cli.Commands;

/// <summary>
/// <c>synclane init curl …</c>: reads the curl, sends it once, learns the recipe from the answer, and writes it with a
/// comment above every field it inferred. The credential in the curl is used for that one request and written nowhere.
/// </summary>
internal static class InitCommand
{
    public static Command Create(CliHost host)
    {
        var output = new Option<string?>("--out", "-o")
        {
            Description = "The file to write. By default, the name of the resource with .jsonc, in the current folder."
        };
        var force = new Option<bool>("--force") { Description = "Replace the file if it exists." };
        var curl = new Argument<string[]>("curl")
        {
            Description = "The curl command, pasted after 'synclane init' as it was copied. Leave it out to pipe it in: pbpaste | synclane init.",
            Arity = ArgumentArity.ZeroOrMore
        };

        var command = new Command("init", "Turns a working curl into a recipe: sends it once and writes what the answer shows, with a comment beside every guess.")
        {
            output, force, curl
        };
        command.SetAction((result, cancellationToken) =>
            RunAsync(result.GetValue(curl) ?? [], result.GetValue(output), result.GetValue(force), host, cancellationToken));
        return command;
    }

    internal static async Task<int> RunAsync(IReadOnlyList<string> words, string? output, bool force, CliHost host, CancellationToken cancellationToken)
    {
        var curl = words.Count > 0 ? CurlCommand.Parse(words)
            : host.Piped is { } piped ? CurlCommand.Parse(await piped.ReadToEndAsync(cancellationToken))
            : SyncResult<CurlCommand>.Failure(CliErrors.CurlWithoutUrl());
        if (!curl.Succeeded)
            return host.Fail(curl.Errors);

        var draft = RecipeDraft.From(curl.Value!);
        var file = output ?? FileName(draft.Recipe.Name);
        var path = Path.GetFullPath(file, host.Directory);
        if (File.Exists(path) && !force)
            return host.Fail(CliErrors.OutputExists(file));

        using var client = host.ProbeClient();
        var probed = await Prober.ProbeAsync(draft, client, cancellationToken);
        if (!probed.Succeeded)
            return host.Fail(probed.Errors);

        var recipe = probed.Value!;
        await File.WriteAllTextAsync(path, RecipeText.Write(recipe.Recipe, recipe.Comments, recipe.Notes), cancellationToken);

        var console = host.Out;
        console.WriteLine($"Wrote {file}.");
        if (recipe.Comments.Count > 0)
            console.WriteLine($"{recipe.Comments.Count} of its fields come from the answer; a comment above each says why.");

        if (recipe.Notes.Count > 0)
        {
            console.WriteLine();
            console.WriteLine("Before you use it:");
            foreach (var note in recipe.Notes)
                console.WriteLine($"  - {note}");
        }

        console.WriteLine();
        console.WriteLine($"Next: synclane test {(file.Contains(' ', StringComparison.Ordinal) ? $"\"{file}\"" : file)}");
        return 0;
    }

    /// <summary>The recipe's resource, such as <c>orders.jsonc</c> for <c>example-shop/orders</c>, safe as a file name.</summary>
    private static string FileName(string recipe)
    {
        var resource = recipe[(recipe.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. resource.Select(character => invalid.Contains(character) ? '-' : character)]) + ".jsonc";
    }
}
