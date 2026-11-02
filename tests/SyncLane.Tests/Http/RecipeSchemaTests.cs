using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

/// <summary>
/// The JSON Schema of a recipe is generated from the recipe types, with the descriptions of their XML comments, so it
/// can never drift from what <see cref="HttpRecipe.Parse"/> reads. Like docs/schema.sql, the file in the repository is
/// compared with what the types describe; run with SYNCLANE_WRITE_SCHEMA=1 to write it after changing a recipe type.
/// </summary>
public partial class RecipeSchemaTests
{
    private const string FileName = "recipe.schema.json";

    [Fact]
    public void The_schema_file_is_the_one_the_recipe_types_describe()
    {
        var generated = Generate();
        var path = Path.Combine(RepositoryRoot(), "schemas", FileName);

        if (Environment.GetEnvironmentVariable("SYNCLANE_WRITE_SCHEMA") == "1")
            File.WriteAllText(path, generated);

        Assert.Equal(generated, File.ReadAllText(path).ReplaceLineEndings("\n"));
        Assert.Equal(generated, HttpRecipe.JsonSchema.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Every_field_of_a_recipe_has_a_description_and_choices_describe_each_value()
    {
        var schema = JsonNode.Parse(Generate())!;

        var undescribed = Properties(schema, "$").Where(field => field.Node["description"] is null).Select(field => field.Path).ToList();

        Assert.Empty(undescribed);
        var types = schema["properties"]!["auth"]!["properties"]!["type"]!;
        Assert.Equal(types["enum"]!.AsArray().Count, types["enumDescriptions"]!.AsArray().Count);
    }

    [Fact]
    public void The_recipes_of_the_live_check_follow_the_schema()
    {
        var schema = JsonNode.Parse(HttpRecipe.JsonSchema)!;
        var recipes = Directory.GetFiles(Path.Combine(RepositoryRoot(), "samples", "LiveCheck", "recipes"), "*.json");

        Assert.NotEmpty(recipes);
        Assert.All(recipes, recipe => Assert.Empty(Problems(schema, Read(File.ReadAllText(recipe)), "$")));
        Assert.All(recipes, recipe => Assert.True(HttpRecipe.Parse(File.ReadAllText(recipe)).Succeeded));
    }

    [Fact]
    public void A_misspelt_field_or_an_unknown_choice_is_caught_by_the_schema_while_the_recipe_is_written()
    {
        var schema = JsonNode.Parse(HttpRecipe.JsonSchema)!;
        var recipe = Read("""
            {
              "name": "test/orders",
              "request": { "url": "https://api.example.com/orders", "header": { "Accept": "application/json" } },
              "auth": { "type": "bearr", "credential": "shop" }
            }
            """);

        Assert.Equal(["$.request: 'header' is not a field here", "$.auth.type: must be one of bearer, basic, apiKey, oauth2, signed"], Problems(schema, recipe, "$"));
    }

    /// <summary>
    /// Checks a document against the keywords this schema uses: type, properties, required, additionalProperties and
    /// enum. Enough to prove that editors will not flag a valid recipe, without a validator package.
    /// </summary>
    private static List<string> Problems(JsonNode schema, JsonNode? value, string path)
    {
        var problems = new List<string>();
        if (schema["enum"] is JsonArray choices)
        {
            if (!choices.Any(choice => JsonNode.DeepEquals(choice, value)))
                problems.Add($"{path}: must be one of {string.Join(", ", choices.Select(choice => choice!.ToString()))}");

            return problems;
        }

        if (schema["type"] is { } type && !Allowed(type).Contains(Kind(value)))
        {
            problems.Add($"{path}: must be {string.Join(" or ", Allowed(type))}");
            return problems;
        }

        if (value is not JsonObject document)
            return problems;

        foreach (var required in schema["required"]?.AsArray() ?? [])
        {
            if (!document.ContainsKey(required!.GetValue<string>()))
                problems.Add($"{path}: '{required}' is required");
        }

        var properties = schema["properties"] as JsonObject;
        foreach (var (name, field) in document)
        {
            if (properties?[name] is { } fieldSchema)
                problems.AddRange(Problems(fieldSchema, field, $"{path}.{name}"));
            else if (schema["additionalProperties"] is JsonObject any)
                problems.AddRange(Problems(any, field, $"{path}.{name}"));
            else if (schema["additionalProperties"]?.GetValue<bool>() == false)
                problems.Add($"{path}: '{name}' is not a field here");
        }

        return problems;
    }

    private static List<string> Allowed(JsonNode type) =>
        type is JsonArray types ? types.Select(each => each!.GetValue<string>()).ToList() : [type.GetValue<string>()];

    private static string Kind(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number when value.ToJsonString().IndexOfAny(['.', 'e', 'E']) < 0 => "integer",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null"
    };

    private static JsonNode? Read(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    /// <summary>The schema of <see cref="HttpRecipe"/>: generated, without nulls, with descriptions.</summary>
    internal static string Generate()
    {
        var documentation = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SyncLane.Http.xml"));
        var summaries = documentation.Descendants("member")
            .Where(member => member.Element("summary") is not null)
            .ToDictionary(member => (string)member.Attribute("name")!, member => Text(member.Element("summary")!));

        var options = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                // "Any JSON" comes as the schema true; an object with no constraint says the same and can carry a description.
                if (node is not JsonObject schema)
                    schema = node?.GetValueKind() == JsonValueKind.False ? new JsonObject { ["not"] = true } : [];

                WithoutNull(schema);
                var type = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
                var description = context.PropertyInfo?.AttributeProvider is MemberInfo member && summaries.TryGetValue($"P:{member.DeclaringType!.FullName}.{member.Name}", out var text)
                    ? text
                    : summaries.GetValueOrDefault($"T:{type.FullName}");

                if (description is not null)
                    schema.Insert(0, "description", description);

                if (type.IsEnum)
                    schema["enumDescriptions"] = new JsonArray(Enum.GetNames(type).Select(name => (JsonNode?)summaries.GetValueOrDefault($"F:{type.FullName}.{name}", "")).ToArray());

                return schema;
            }
        };

        var root = (JsonObject)JsonSchemaExporter.GetJsonSchemaAsNode(RecipeJson.Default.HttpRecipe, options);
        root.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
        root.Insert(1, "$id", "https://raw.githubusercontent.com/ferhatblnk/synclane/main/schemas/recipe.schema.json");
        root.Insert(2, "title", "SyncLane HTTP recipe");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Optional fields in a recipe are left out, never written as null, so the schema does not offer null.</summary>
    private static void WithoutNull(JsonObject schema)
    {
        if (schema["type"] is JsonArray types)
        {
            var kept = types.Where(type => type?.GetValue<string>() != "null").Select(type => type!.GetValue<string>()).ToList();
            schema["type"] = kept.Count == 1 ? kept[0] : new JsonArray(kept.Select(type => (JsonNode?)type).ToArray());
        }

        if (schema["enum"] is JsonArray values)
            schema["enum"] = new JsonArray(values.Where(value => value is not null).Select(value => value!.DeepClone()).ToArray());
    }

    /// <summary>An XML comment as one line of text: references by their short names, code in backticks.</summary>
    private static string Text(XElement summary)
    {
        var text = string.Concat(summary.Nodes().Select(node => node switch
        {
            XText plain => plain.Value,
            XElement { Name.LocalName: "see" } see when see.Attribute("cref") is { } cref => cref.Value[(cref.Value.LastIndexOf('.') + 1)..],
            XElement { Name.LocalName: "see" } see => see.Attribute("langword")?.Value ?? see.Value,
            XElement { Name.LocalName: "c" } code => $"`{code.Value}`",
            XElement { Name.LocalName: "paramref" or "typeparamref" } reference => reference.Attribute("name")?.Value ?? "",
            XElement element => element.Value,
            _ => ""
        }));

        return Spaces().Replace(text, " ").Trim();
    }

    private static IEnumerable<(string Path, JsonNode Node)> Properties(JsonNode schema, string path)
    {
        if (schema["properties"] is not JsonObject properties)
            yield break;

        foreach (var (name, node) in properties)
        {
            yield return ($"{path}.{name}", node!);
            foreach (var nested in Properties(node!["items"] ?? node, $"{path}.{name}"))
                yield return nested;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SyncLane.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
