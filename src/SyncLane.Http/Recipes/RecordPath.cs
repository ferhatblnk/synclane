using System.Text.Json;
using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>Where the records are in a JSON answer: <c>$</c>, or <c>$.data.items</c>. Only property names, no filters.</summary>
internal sealed partial class RecordPath
{
    public const string Root = "$";

    private readonly string[] _segments;

    private RecordPath(string text, string[] segments)
    {
        Text = text;
        _segments = segments;
    }

    public string Text { get; }

    /// <summary>Reads <paramref name="text"/>, or returns <c>null</c> when it is not in the supported syntax.</summary>
    public static RecordPath? Parse(string? text)
    {
        text ??= Root;
        return Syntax().IsMatch(text) ? new RecordPath(text, text.Split('.')[1..]) : null;
    }

    /// <summary>The value at this path, if there is one.</summary>
    public bool TryFind(JsonElement root, out JsonElement value)
    {
        value = root;
        foreach (var segment in _segments)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }

        return true;
    }

    /// <summary>The array at this path, or why there is none.</summary>
    public SyncResult<JsonElement> Find(JsonElement root, string recipe)
    {
        if (!TryFind(root, out var current))
            return SyncResult<JsonElement>.Failure(HttpErrors.RecordsNotFound(recipe, Text));

        return current.ValueKind == JsonValueKind.Array
            ? SyncResult<JsonElement>.Success(current)
            : SyncResult<JsonElement>.Failure(HttpErrors.RecordsNotArray(recipe, Text, current.ValueKind.ToString().ToLowerInvariant()));
    }

    [GeneratedRegex(@"^\$(\.[A-Za-z0-9_\-]+)*$")]
    private static partial Regex Syntax();
}
