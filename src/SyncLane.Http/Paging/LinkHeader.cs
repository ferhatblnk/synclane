using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>Reads the <c>rel="next"</c> target of a <c>Link</c> header (RFC 8288), as many APIs send it.</summary>
internal static partial class LinkHeader
{
    /// <summary>The URL marked <c>next</c>, or <c>null</c>.</summary>
    public static string? Next(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            foreach (Match link in Link().Matches(value))
            {
                var relations = link.Groups["rel"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (relations.Contains("next", StringComparer.OrdinalIgnoreCase))
                    return link.Groups["url"].Value;
            }
        }

        return null;
    }

    /// <summary>One link: a URL in angle brackets, then parameters up to the next link, one of them <c>rel</c>.</summary>
    [GeneratedRegex("""<(?<url>[^>]*)>[^<]*?\brel\s*=\s*"?(?<rel>[^";,]*)"?""", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
}
