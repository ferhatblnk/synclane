using System.Text.Json;
using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>
/// A recipe checked once and split into parts ready to render. Building a request for a page does no parsing and no
/// validation of the recipe; only the values that change per job are checked.
/// </summary>
internal sealed partial class CompiledRecipe
{
    private const string Unnamed = "(unnamed)";

    /// <summary>Headers a recipe may not set: they decide where and how the request travels, which is not the recipe's call.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection"
    };

    private CompiledRecipe(
        HttpRecipe recipe,
        HttpMethod method,
        Template url,
        IReadOnlyList<(string Name, Template Value)> query,
        IReadOnlyList<(string Name, Template Value)> headers,
        IReadOnlyDictionary<string, Template> bodyStrings,
        RecordPath records,
        bool usesWindow,
        IPaginator paginator)
    {
        Recipe = recipe;
        Method = method;
        Url = url;
        Query = query;
        Headers = headers;
        BodyStrings = bodyStrings;
        Records = records;
        UsesWindow = usesWindow;
        Paginator = paginator;
    }

    public HttpRecipe Recipe { get; }

    public string Name => Recipe.Name;

    public HttpMethod Method { get; }

    public Template Url { get; }

    public IReadOnlyList<(string Name, Template Value)> Query { get; }

    public IReadOnlyList<(string Name, Template Value)> Headers { get; }

    /// <summary>Every string value of the body that contains a placeholder, by its original text.</summary>
    public IReadOnlyDictionary<string, Template> BodyStrings { get; }

    public RecordPath Records { get; }

    public WindowFormat WindowFormat => Recipe.Window?.Format ?? WindowFormat.Iso8601;

    public bool UsesWindow { get; }

    public IPaginator Paginator { get; }

    /// <summary>Checks <paramref name="recipe"/> and returns every problem found, or the compiled recipe.</summary>
    public static SyncResult<CompiledRecipe> Compile(HttpRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var errors = new List<SyncError>();
        var name = string.IsNullOrWhiteSpace(recipe.Name) ? Unnamed : recipe.Name;
        if (name == Unnamed)
            errors.Add(HttpErrors.FieldMissing(name, "name"));

        if (recipe.Request is not { } request)
        {
            errors.Add(HttpErrors.FieldMissing(name, "request"));
            return SyncResult<CompiledRecipe>.Failure(errors);
        }

        var placeholders = new List<(string Placeholder, string Location)>();

        var method = ParseMethod(request.Method);
        if (method is null)
            errors.Add(HttpErrors.MethodUnsupported(name, request.Method));

        var url = CheckUrl(name, request, errors, out var origin);
        if (url is not null)
            placeholders.AddRange(url.Placeholders.Select(placeholder => (placeholder, "request.url")));

        var query = new List<(string, Template)>();
        foreach (var (key, value) in request.Query)
        {
            var location = $"request.query.{key}";
            if (string.IsNullOrWhiteSpace(key))
                errors.Add(HttpErrors.FieldMissing(name, "request.query name"));
            else if (Parse(name, value, location, errors) is { } template)
            {
                query.Add((key, template));
                placeholders.AddRange(template.Placeholders.Select(placeholder => (placeholder, location)));
            }
        }

        var headers = new List<(string, Template)>();
        foreach (var (key, value) in request.Headers)
        {
            var location = $"request.headers.{key}";
            if (!HeaderName().IsMatch(key) || ReservedHeaders.Contains(key) || value is null || value.AsSpan().ContainsAny('\r', '\n'))
                errors.Add(HttpErrors.HeaderInvalid(name, key));
            else if (Parse(name, value, location, errors) is { } template)
            {
                headers.Add((key, template));
                placeholders.AddRange(template.Placeholders.Select(placeholder => (placeholder, location)));
            }
        }

        var bodyStrings = new Dictionary<string, Template>(StringComparer.Ordinal);
        if (request.Body is { } body)
        {
            if (method == HttpMethod.Get)
                errors.Add(HttpErrors.BodyNotAllowed(name, method.Method));

            CollectBodyStrings(name, body, bodyStrings, errors);
            placeholders.AddRange(bodyStrings.Values.SelectMany(template => template.Placeholders).Select(placeholder => (placeholder, "request.body")));
        }

        foreach (var (placeholder, location) in placeholders.Where(found => !Placeholders.IsKnown(found.Placeholder)))
            errors.Add(HttpErrors.PlaceholderUnknown(name, placeholder, location));

        var records = RecordPath.Parse(recipe.Records);
        if (records is null)
            errors.Add(HttpErrors.RecordsPathInvalid(name, recipe.Records ?? ""));

        if (recipe.Window is { } window && !Enum.IsDefined(window.Format))
            errors.Add(HttpErrors.WindowFormatUnknown(name, window.Format.ToString()));

        // Paging is checked only against a valid URL, because a next-page URL is compared with the recipe's host.
        var paginator = origin is null ? null : PaginationCompiler.Compile(name, recipe.Pagination, origin, request.Query, errors);

        if (errors.Count > 0)
            return SyncResult<CompiledRecipe>.Failure(errors);

        var usesWindow = placeholders.Any(found => Placeholders.IsWindow(found.Placeholder));
        return SyncResult<CompiledRecipe>.Success(
            new CompiledRecipe(recipe, method!, url!, query, headers, bodyStrings, records!, usesWindow, paginator!));
    }

    private static HttpMethod? ParseMethod(string? method) => method?.ToUpperInvariant() switch
    {
        "GET" => HttpMethod.Get,
        "POST" => HttpMethod.Post,
        _ => null
    };

    private static Template? CheckUrl(string name, HttpRecipeRequest request, List<SyncError> errors, out Origin? origin)
    {
        origin = null;
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            errors.Add(HttpErrors.FieldMissing(name, "request.url"));
            return null;
        }

        var template = Parse(name, request.Url, "request.url", errors);
        if (template is null)
            return null;

        var schemeEnd = request.Url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            errors.Add(HttpErrors.UrlInvalid(name, "is not an absolute URL"));
            return null;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = request.Url.IndexOfAny(['/', '?', '#'], authorityStart);
        var beforePath = request.Url[..(authorityEnd < 0 ? request.Url.Length : authorityEnd)];
        if (beforePath.Contains("{{", StringComparison.Ordinal))
        {
            errors.Add(HttpErrors.PlaceholderInHost(name));
            return null;
        }

        // Placeholders only appear after the host, so filling them with a plain letter checks the rest of the URL.
        var sample = template.Render(_ => "x");
        if (!Uri.TryCreate(sample, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            errors.Add(HttpErrors.UrlInvalid(name, "is not an absolute URL"));
        else if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && request.AllowHttp))
            errors.Add(HttpErrors.UrlInvalid(name, "must use https"));
        else if (!string.IsNullOrEmpty(uri.UserInfo))
            errors.Add(HttpErrors.UrlInvalid(name, "must not contain a user name or password; put credentials in headers"));
        else
        {
            origin = Origin.Of(uri);
            return template;
        }

        return null;
    }

    private static void CollectBodyStrings(string name, JsonElement element, Dictionary<string, Template> found, List<SyncError> errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    CollectBodyStrings(name, property.Value, found, errors);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectBodyStrings(name, item, found, errors);
                break;

            case JsonValueKind.String:
                var text = element.GetString()!;
                if (text.Contains("{{", StringComparison.Ordinal) && !found.ContainsKey(text) && Parse(name, text, "request.body", errors) is { } template)
                    found[text] = template;
                break;
        }
    }

    private static Template? Parse(string name, string? text, string location, List<SyncError> errors)
    {
        var template = Template.Parse(text ?? "");
        if (template is null)
            errors.Add(HttpErrors.TemplateMalformed(name, location));

        return template;
    }

    /// <summary>An HTTP token as RFC 9110 defines it.</summary>
    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$")]
    private static partial Regex HeaderName();
}
