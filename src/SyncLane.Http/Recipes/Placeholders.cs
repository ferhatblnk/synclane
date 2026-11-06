using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>The values a recipe can refer to. Anything else is refused when the recipe is loaded.</summary>
internal static partial class Placeholders
{
    public const string WindowStart = "window.start";
    public const string WindowEnd = "window.end";
    public const string OwnerKey = "owner.key";

    /// <summary>A new UUID for every request sent, the same wherever it appears in that request.</summary>
    public const string RequestId = "request.id";

    public static readonly IReadOnlyList<string> Known = [WindowStart, WindowEnd, OwnerKey, RequestId];

    /// <summary>How the available placeholders are named in a message, an owner's values included.</summary>
    public static string Available => string.Join(", ", Known.Select(name => $"{{{{{name}}}}}")) + ", {{owner.<value>}} for one of the owner's values";

    public static bool IsKnown(string name) => Known.Contains(name, StringComparer.Ordinal) || OwnerValue(name) is not null;

    /// <summary>The name of the owner's value <paramref name="name"/> refers to, such as <c>region</c> for <c>owner.region</c>; else <c>null</c>.</summary>
    public static string? OwnerValue(string name) =>
        name.StartsWith("owner.", StringComparison.Ordinal) && name != OwnerKey && ValueName().IsMatch(name.AsSpan(6)) ? name[6..] : null;

    /// <summary>A placeholder that comes from the owner, which alone may make up a host.</summary>
    public static bool IsOwner(string name) => name == OwnerKey || OwnerValue(name) is not null;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ValueName();

    public static bool IsWindow(string name) => name is WindowStart or WindowEnd;
}
