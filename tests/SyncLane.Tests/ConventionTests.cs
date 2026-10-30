using System.Reflection;
using System.Text.RegularExpressions;

namespace SyncLane.Tests;

/// <summary>
/// The rules in CLAUDE.md that a machine can check. Each test reads the source of the library, so a rule that is
/// broken fails the build instead of waiting for a reviewer to notice.
/// </summary>
public partial class ConventionTests
{
    private static readonly string Root = FindRoot();

    private static readonly IReadOnlyList<SourceFile> Library = Directory
        .EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        .Select(path => new SourceFile(Path.GetRelativePath(Root, path), File.ReadAllLines(path)))
        .ToList();

    private static IEnumerable<FieldInfo> Codes => typeof(SyncErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(constant => constant.IsLiteral);

    [Fact]
    public void Error_codes_are_unique_and_follow_the_numbering()
    {
        var values = Codes.Select(code => (string)code.GetRawConstantValue()!).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.All(values, value => Assert.Matches("^SL[1-4][0-9]{3}$", value));
    }

    [Fact]
    public void Every_error_code_is_documented_and_every_documented_code_exists()
    {
        var page = File.ReadAllText(Path.Combine(Root, "docs", "errors.md"));
        var values = Codes.Select(code => (string)code.GetRawConstantValue()!).ToHashSet();
        var documented = DocumentedCode().Matches(page).Select(match => match.Groups[1].Value).ToHashSet();

        Assert.Empty(values.Except(documented));
        Assert.Empty(documented.Except(values));
    }

    [Fact]
    public void Every_error_code_has_a_message_in_the_catalog()
    {
        var catalog = Library.Single(file => file.Path.EndsWith("SyncLaneErrors.cs", StringComparison.Ordinal)).Text;

        Assert.All(Codes, code => Assert.Contains($"SyncErrorCodes.{code.Name},", catalog));
    }

    [Fact]
    public void No_exception_is_created_with_a_message_written_in_place()
    {
        var offenders = Library
            .SelectMany(file => file.Lines.Select((line, index) => (file.Path, Line: index + 1, Text: line)))
            .Where(line => InlineMessage().IsMatch(line.Text))
            .Select(line => $"{line.Path}:{line.Line}: {line.Text.Trim()}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_loop_that_awaits_says_why_it_must_be_sequential()
    {
        var offenders = Library.SelectMany(file => AwaitingLoops(file)
            .Where(loop => !HasSequentialComment(file.Lines, loop))
            .Select(loop => $"{file.Path}:{loop + 1}: {file.Lines[loop].Trim()}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Nothing_blocks_on_a_task()
    {
        var offenders = Library
            .SelectMany(file => file.Lines.Select((line, index) => (file.Path, Line: index + 1, Text: line)))
            .Where(line => Blocking().IsMatch(line.Text))
            .Select(line => $"{line.Path}:{line.Line}: {line.Text.Trim()}");

        Assert.Empty(offenders);
    }

    /// <summary>Line numbers of loop headers whose header or body awaits.</summary>
    private static IEnumerable<int> AwaitingLoops(SourceFile file)
    {
        var lines = file.Lines;
        for (var header = 0; header < lines.Length; header++)
        {
            var trimmed = lines[header].TrimStart();
            var isDo = trimmed == "do";
            if (!isDo && !(LoopHeader().IsMatch(trimmed) && !trimmed.EndsWith(';')))
                continue;

            var indent = Indent(lines[header]);
            var body = new List<string> { lines[header] };
            var next = header + 1;

            if (next < lines.Length && lines[next].Trim() == "{" && Indent(lines[next]) == indent)
            {
                while (++next < lines.Length && !(Indent(lines[next]) == indent && lines[next].TrimStart().StartsWith('}')))
                    body.Add(lines[next]);
            }
            else
            {
                while (next < lines.Length && (lines[next].Trim().Length == 0 || Indent(lines[next]) > indent))
                    body.Add(lines[next++]);
                next--;
            }

            if (isDo && next + 1 < lines.Length)
                body.Add(lines[next + 1]);

            if (body.Any(line => line.Contains("await ", StringComparison.Ordinal)))
                yield return header;
        }
    }

    private static bool HasSequentialComment(string[] lines, int header)
    {
        for (var above = header - 1; above >= 0 && lines[above].TrimStart().StartsWith("//", StringComparison.Ordinal); above--)
        {
            if (lines[above].Contains("Sequential:", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SyncLane.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"`(SL[0-9]{4})`")]
    private static partial Regex DocumentedCode();

    [GeneratedRegex(@"new\s+[A-Za-z]*Exception\s*\(\s*[$@]*""")]
    private static partial Regex InlineMessage();

    [GeneratedRegex(@"^(await\s+)?(foreach|for|while)\s*\(")]
    private static partial Regex LoopHeader();

    [GeneratedRegex(@"\.Wait\(\)|\.GetAwaiter\(\)\.GetResult\(\)|(?<![A-Za-z])\.Result\b")]
    private static partial Regex Blocking();

    private sealed record SourceFile(string Path, string[] Lines)
    {
        public string Text { get; } = string.Join('\n', Lines);
    }
}
