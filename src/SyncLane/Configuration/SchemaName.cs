using System.Text.RegularExpressions;

namespace SyncLane;

/// <summary>The schema name is placed into SQL as an identifier, so only plain lower-case identifiers are accepted.</summary>
internal static partial class SchemaName
{
    public static bool IsValid(string? schema) => schema is not null && Pattern().IsMatch(schema);

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Pattern();
}
