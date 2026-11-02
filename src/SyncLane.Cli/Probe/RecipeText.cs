using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SyncLane.Http;

namespace SyncLane.Cli.Probe;

/// <summary>
/// Writes a recipe as people write them by hand: <c>jsonc</c>, small sections on one line, and above each guessed field
/// a comment that says why it holds what it does. The notes go at the top, so the file explains itself.
/// </summary>
internal static class RecipeText
{
    private const int InlineWidth = 100;

    // The file is read and edited by people, never embedded in a page: "+" and "ü" stay as they are, not "\u002B".
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Write(HttpRecipe recipe, IReadOnlyDictionary<string, string> comments, IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var node = (JsonObject)JsonSerializer.SerializeToNode(recipe, Options)!;
        var request = (JsonObject)node["request"]!;
        Prune(request, "method", "GET");
        Prune(request, "allowHttp", false);
        foreach (var empty in new[] { "query", "headers" }.Where(name => request[name] is JsonObject { Count: 0 }))
            request.Remove(empty);

        var text = new StringBuilder("{\n");
        foreach (var note in notes)
            text.Append("  // ").Append(note).Append('\n');

        Members(node, "", 1, comments, text);
        return text.Append("}\n").ToString();
    }

    private static void Members(JsonObject node, string path, int depth, IReadOnlyDictionary<string, string> comments, StringBuilder text)
    {
        var indent = new string(' ', depth * 2);
        var index = 0;
        foreach (var (name, value) in node)
        {
            var child = path.Length == 0 ? name : $"{path}.{name}";
            if (comments.TryGetValue(child, out var comment))
                text.Append(indent).Append("// ").Append(comment).Append('\n');

            text.Append(indent).Append(JsonSerializer.Serialize(name, Options)).Append(": ");
            if (value is JsonObject nested && !Inline(nested, child, comments))
            {
                text.Append("{\n");
                Members(nested, child, depth + 1, comments, text);
                text.Append(indent).Append('}');
            }
            else
            {
                text.Append(OneLine(value));
            }

            text.Append(++index < node.Count ? ",\n" : "\n");
        }
    }

    /// <summary>A section goes on one line when it is short, holds no section of its own, and no field in it has a comment.</summary>
    private static bool Inline(JsonObject node, string path, IReadOnlyDictionary<string, string> comments) =>
        node.All(member => member.Value is not JsonObject)
        && !comments.Keys.Any(key => key.StartsWith(path + ".", StringComparison.Ordinal))
        && OneLine(node).Length <= InlineWidth;

    private static string OneLine(JsonNode? value) => value switch
    {
        JsonObject node => node.Count == 0 ? "{}" : "{ " + string.Join(", ", node.Select(member => $"{JsonSerializer.Serialize(member.Key, Options)}: {OneLine(member.Value)}")) + " }",
        null => "null",
        _ => value.ToJsonString(Options)
    };

    private static void Prune<T>(JsonObject node, string name, T standard)
    {
        if (node[name] is JsonValue value && value.TryGetValue<T>(out var actual) && EqualityComparer<T>.Default.Equals(actual, standard))
            node.Remove(name);
    }
}
