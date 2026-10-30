using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncLane.Http;

/// <summary>
/// How to fetch one job type over HTTP: the request, where the records are in the answer and how the job's window is
/// written. Build it in code or load it from JSON with <see cref="Parse"/>; the format is described in docs/recipes.md.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpRecipe
{
    /// <summary>The JSON Schema the file follows. Ignored by SyncLane; lets editors check the file.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>A name for messages and logs, such as <c>example-shop/orders</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The request to send for each page.</summary>
    public required HttpRecipeRequest Request { get; init; }

    /// <summary>Where the array of records is in the JSON answer: <c>$</c> for the root, or <c>$.data.items</c>. Defaults to the root.</summary>
    public string? Records { get; init; }

    /// <summary>How the job's window is written into the request. Defaults to ISO 8601 in UTC.</summary>
    public HttpRecipeWindow? Window { get; init; }

    /// <summary>
    /// Reads a recipe from JSON and validates it. Every problem found is returned at once, each with its code.
    /// </summary>
    public static SyncResult<HttpRecipe> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        HttpRecipe? recipe;
        try
        {
            recipe = JsonSerializer.Deserialize(json, RecipeJson.Default.HttpRecipe);
        }
        catch (JsonException exception)
        {
            return SyncResult<HttpRecipe>.Failure(HttpErrors.RecipeJsonInvalid(exception.Message));
        }

        if (recipe is null)
            return SyncResult<HttpRecipe>.Failure(HttpErrors.RecipeJsonInvalid("The file is empty or null."));

        var compiled = CompiledRecipe.Compile(recipe);
        return compiled.Succeeded ? SyncResult<HttpRecipe>.Success(recipe) : SyncResult<HttpRecipe>.Failure(compiled.Errors);
    }

    /// <summary>Checks the recipe and returns every problem found, each with its code.</summary>
    public SyncResult Validate()
    {
        var compiled = CompiledRecipe.Compile(this);
        return compiled.Succeeded ? SyncResult.Success() : SyncResult.Failure(compiled.Errors);
    }
}

/// <summary>The request a recipe sends. Strings may contain placeholders such as <c>{{window.start}}</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpRecipeRequest
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    // The JSON source generator sets a missing init property to null instead of skipping it, so the defaults are
    // applied in the setters, where both JSON and code go through them.
    private readonly string _method = "GET";
    private readonly IReadOnlyDictionary<string, string> _query = None;
    private readonly IReadOnlyDictionary<string, string> _headers = None;

    /// <summary><c>GET</c> or <c>POST</c>. Defaults to <c>GET</c>.</summary>
    public string Method { get => _method; init => _method = value ?? "GET"; }

    /// <summary>
    /// The absolute https URL. Placeholders may appear in the path and the query, never in the scheme, host or port,
    /// so a recipe can only call the host it names. Placeholder values are escaped.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>Query parameters added to the URL. Values may contain placeholders and are escaped.</summary>
    public IReadOnlyDictionary<string, string> Query { get => _query; init => _query = value ?? None; }

    /// <summary>Request headers. Values may contain placeholders.</summary>
    public IReadOnlyDictionary<string, string> Headers { get => _headers; init => _headers = value ?? None; }

    /// <summary>
    /// A JSON body for POST. Placeholders are filled in string values only, and written as JSON strings, so a value
    /// can never change the shape of the body.
    /// </summary>
    public JsonElement? Body { get; init; }

    /// <summary>Allow a plain http URL. Meant for local test servers; leave it off for real APIs.</summary>
    public bool AllowHttp { get; init; }
}

/// <summary>How the job's window is written into the request.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpRecipeWindow
{
    /// <summary>The format of <c>{{window.start}}</c> and <c>{{window.end}}</c>. Always in UTC.</summary>
    public WindowFormat Format { get; init; } = WindowFormat.Iso8601;
}

/// <summary>How a moment of the window is written. Every format is in UTC.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WindowFormat>))]
public enum WindowFormat
{
    /// <summary><c>2026-10-30T12:00:00Z</c>.</summary>
    [JsonStringEnumMemberName("iso8601")]
    Iso8601,

    /// <summary>Seconds since 1970-01-01, such as <c>1793361600</c>.</summary>
    [JsonStringEnumMemberName("unix")]
    Unix,

    /// <summary>Milliseconds since 1970-01-01.</summary>
    [JsonStringEnumMemberName("unixMilliseconds")]
    UnixMilliseconds,

    /// <summary>The date only, <c>2026-10-30</c>. Use it only when windows start and end at midnight.</summary>
    [JsonStringEnumMemberName("date")]
    Date
}

/// <summary>The JSON contract of a recipe, generated at compile time.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(HttpRecipe))]
internal sealed partial class RecipeJson : JsonSerializerContext;
