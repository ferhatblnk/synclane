namespace SyncLane.Http;

/// <summary>The values a recipe can refer to. Anything else is refused when the recipe is loaded.</summary>
internal static class Placeholders
{
    public const string WindowStart = "window.start";
    public const string WindowEnd = "window.end";
    public const string OwnerKey = "owner.key";

    public static readonly IReadOnlyList<string> Known = [WindowStart, WindowEnd, OwnerKey];

    public static bool IsKnown(string name) => Known.Contains(name, StringComparer.Ordinal);

    public static bool IsWindow(string name) => name is WindowStart or WindowEnd;
}
