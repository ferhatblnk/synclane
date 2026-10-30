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

    /// <summary>How to get the next page. Leave it out when one request returns everything.</summary>
    public HttpRecipePagination? Pagination { get; init; }

    /// <summary>
    /// How requests are authenticated. Leave it out for an open API. A recipe never holds a secret: it names the
    /// credential, and the <see cref="ICredentialStore"/> gives its values for each owner.
    /// </summary>
    public HttpRecipeAuth? Auth { get; init; }

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

    /// <summary>
    /// Request headers. Values may contain placeholders. <c>Authorization</c> and <c>Proxy-Authorization</c> are refused:
    /// credentials go through <see cref="HttpRecipe.Auth"/>, never into a recipe.
    /// </summary>
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

/// <summary>
/// How to get the next page. Which fields apply depends on <see cref="Type"/>; a field that does not apply is an error,
/// so a recipe never looks like it does something it does not. The rules per type are in docs/recipes.md.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpRecipePagination
{
    /// <summary>How the API pages.</summary>
    public required PaginationType Type { get; init; }

    /// <summary>The query parameter that carries the page number, the offset or the cursor.</summary>
    public string? Param { get; init; }

    /// <summary>The first page number (default 1) or the first offset (default 0).</summary>
    public int? Start { get; init; }

    /// <summary>
    /// Records per page. With <see cref="PaginationType.Page"/> a shorter page is the last one, which saves a request;
    /// without it the last page is the first empty one. Required for <see cref="PaginationType.Offset"/>.
    /// </summary>
    public int? Size { get; init; }

    /// <summary>A query parameter to send <see cref="Size"/> in, such as <c>limit</c> or <c>per_page</c>.</summary>
    public string? SizeParam { get; init; }

    /// <summary>
    /// For <see cref="PaginationType.Cursor"/>: where the next cursor is in the answer, such as <c>$.meta.next</c>.
    /// For <see cref="PaginationType.NextUrl"/>: where the next page's URL is, such as <c>$.links.next</c>.
    /// </summary>
    public string? From { get; init; }

    /// <summary>For <see cref="PaginationType.Cursor"/>: take the cursor from this property of the page's last record instead, such as <c>id</c>.</summary>
    public string? FromLastRecord { get; init; }

    /// <summary>For <see cref="PaginationType.Cursor"/>: a boolean in the answer that is <c>false</c> on the last page, such as <c>$.has_more</c>.</summary>
    public string? HasMore { get; init; }
}

/// <summary>
/// How requests are authenticated. Which fields apply depends on <see cref="Type"/>; a field that does not apply is an
/// error. The rules per type are in docs/recipes.md.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpRecipeAuth
{
    /// <summary>The authentication scheme.</summary>
    public required AuthType Type { get; init; }

    /// <summary>
    /// The name the credential store finds the values under, together with the job's owner. Recipes for one account
    /// share a name, such as <c>example-shop</c>.
    /// </summary>
    public required string Credential { get; init; }

    /// <summary>For <see cref="AuthType.ApiKey"/>: where the key goes. Defaults to a header.</summary>
    public ApiKeyLocation? In { get; init; }

    /// <summary>For <see cref="AuthType.ApiKey"/>: the header or query parameter that carries the key, such as <c>X-Api-Key</c>.</summary>
    public string? Name { get; init; }

    /// <summary>For <see cref="AuthType.ApiKey"/> in a header: text written before the key, such as <c>Token </c>.</summary>
    public string? Prefix { get; init; }

    /// <summary>For <see cref="AuthType.OAuth2"/>: how the access token is obtained.</summary>
    public OAuth2Grant? Grant { get; init; }

    /// <summary>For <see cref="AuthType.OAuth2"/>: the absolute https URL of the token endpoint. No placeholders.</summary>
    public string? TokenUrl { get; init; }

    /// <summary>For <see cref="AuthType.OAuth2"/>: the scope to ask for, such as <c>orders:read</c>. Left out, none is sent.</summary>
    public string? Scope { get; init; }

    /// <summary>For <see cref="AuthType.OAuth2"/>: how the client id and secret reach the token endpoint. Defaults to <see cref="OAuth2ClientAuth.Basic"/>.</summary>
    public OAuth2ClientAuth? ClientAuth { get; init; }

    /// <summary>For <see cref="AuthType.Signed"/>: the name the <see cref="IRequestSigner"/> was registered under.</summary>
    public string? Signer { get; init; }
}

/// <summary>How requests are authenticated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthType>))]
public enum AuthType
{
    /// <summary><c>Authorization: Bearer</c> with the credential's <see cref="Http.Credential.Token"/>.</summary>
    [JsonStringEnumMemberName("bearer")]
    Bearer,

    /// <summary><c>Authorization: Basic</c> with the credential's user name and password.</summary>
    [JsonStringEnumMemberName("basic")]
    Basic,

    /// <summary>The credential's <see cref="Http.Credential.Token"/> in a header or query parameter of the recipe's choice.</summary>
    [JsonStringEnumMemberName("apiKey")]
    ApiKey,

    /// <summary>An OAuth 2.0 access token, fetched from the token endpoint, kept in memory and renewed before it expires.</summary>
    [JsonStringEnumMemberName("oauth2")]
    OAuth2,

    /// <summary>Each request signed by an <see cref="IRequestSigner"/> you register.</summary>
    [JsonStringEnumMemberName("signed")]
    Signed
}

/// <summary>Where an API key goes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApiKeyLocation>))]
public enum ApiKeyLocation
{
    /// <summary>In a request header.</summary>
    [JsonStringEnumMemberName("header")]
    Header,

    /// <summary>In a query parameter. Prefer a header when the API accepts one: a URL is recorded in more places.</summary>
    [JsonStringEnumMemberName("query")]
    Query
}

/// <summary>How an OAuth 2.0 access token is obtained.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuth2Grant>))]
public enum OAuth2Grant
{
    /// <summary>The client credentials grant: the client id and secret alone.</summary>
    [JsonStringEnumMemberName("clientCredentials")]
    ClientCredentials,

    /// <summary>The refresh token grant: a refresh token the owner granted, for example through a consent screen in your application.</summary>
    [JsonStringEnumMemberName("refreshToken")]
    RefreshToken
}

/// <summary>How the client id and secret reach the token endpoint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuth2ClientAuth>))]
public enum OAuth2ClientAuth
{
    /// <summary>In an <c>Authorization: Basic</c> header, as RFC 6749 recommends.</summary>
    [JsonStringEnumMemberName("basic")]
    Basic,

    /// <summary>As <c>client_id</c> and <c>client_secret</c> in the form body, for endpoints that only accept that.</summary>
    [JsonStringEnumMemberName("body")]
    Body
}

/// <summary>How an API pages.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PaginationType>))]
public enum PaginationType
{
    /// <summary>A page number in a query parameter: 1, 2, 3.</summary>
    [JsonStringEnumMemberName("page")]
    Page,

    /// <summary>A record offset in a query parameter: 0, 100, 200.</summary>
    [JsonStringEnumMemberName("offset")]
    Offset,

    /// <summary>A token from the answer, sent back in a query parameter.</summary>
    [JsonStringEnumMemberName("cursor")]
    Cursor,

    /// <summary>The full URL of the next page, in the answer.</summary>
    [JsonStringEnumMemberName("nextUrl")]
    NextUrl,

    /// <summary>The URL of the next page in the <c>Link</c> header, with <c>rel="next"</c>.</summary>
    [JsonStringEnumMemberName("linkHeader")]
    LinkHeader
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
