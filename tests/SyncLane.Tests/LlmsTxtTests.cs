using System.Text.RegularExpressions;

namespace SyncLane.Tests;

/// <summary>llms.txt is what an AI assistant reads first; a link that leads nowhere or a guide it leaves out misleads it.</summary>
public partial class LlmsTxtTests
{
    private const string Raw = "https://raw.githubusercontent.com/ferhatblnk/synclane/main/";

    [Fact]
    public void Every_link_in_llms_txt_leads_to_a_file_in_the_repository()
    {
        var root = Repository();
        var links = Link().Matches(File.ReadAllText(Path.Combine(root, "llms.txt"))).Select(match => match.Groups["url"].Value).ToList();

        Assert.NotEmpty(links);
        Assert.All(links, url =>
        {
            Assert.StartsWith(Raw, url, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, url[Raw.Length..])), $"{url} leads to no file in the repository.");
        });
    }

    [Fact]
    public void Every_guide_in_docs_is_in_llms_txt()
    {
        var root = Repository();
        var text = File.ReadAllText(Path.Combine(root, "llms.txt"));

        Assert.All(Directory.GetFiles(Path.Combine(root, "docs"), "*.md"), guide =>
            Assert.Contains($"{Raw}docs/{Path.GetFileName(guide)}", text));
    }

    [Fact]
    public void It_has_the_shape_of_an_llms_txt_a_title_a_summary_and_sections_of_links()
    {
        var lines = File.ReadAllLines(Path.Combine(Repository(), "llms.txt"));

        Assert.StartsWith("# ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("> ", lines.First(line => line.Length > 0 && !line.StartsWith('#')), StringComparison.Ordinal);
        Assert.Contains("## Docs", lines);
    }

    private static string Repository()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "CLAUDE.md")))
            folder = folder.Parent;

        return folder?.FullName ?? throw new DirectoryNotFoundException(AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\]\((?<url>https?://[^)\s]+)\)")]
    private static partial Regex Link();
}
