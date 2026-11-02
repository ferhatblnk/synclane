using System.Text;
using System.Text.Json;
using SyncLane.Http;

namespace SyncLane.Cli.Curl;

/// <summary>
/// The first draft of a recipe, from a curl command alone: its request with the query taken apart, its credential moved
/// into an <c>auth</c> section that only names it, and browser noise left out. The secret itself is kept in memory for
/// the one request that checks the draft, and never written anywhere.
/// </summary>
internal sealed record RecipeDraft(HttpRecipe Recipe, IReadOnlyList<string> Notes, DraftSecret? Secret)
{
    /// <summary>Headers a browser adds that say nothing about the API, or that a recipe must not carry.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept-encoding", "accept-language", "cache-control", "pragma", "priority", "referer", "origin", "dnt",
        "upgrade-insecure-requests", "user-agent", "connection", "host", "content-length", "cookie", "if-none-match",
        "if-modified-since", "x-requested-with"
    };

    /// <summary>Query parameters that carry a credential.</summary>
    private static readonly HashSet<string> SecretParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key", "apikey", "api-key", "key", "access_token", "token", "auth", "auth_token", "client_secret", "secret", "password"
    };

    /// <summary>Header names that are not credentials although they look like it.</summary>
    private static readonly HashSet<string> NotSecret = new(StringComparer.OrdinalIgnoreCase) { "idempotency-key", "x-request-key" };

    public static RecipeDraft From(CurlCommand curl)
    {
        ArgumentNullException.ThrowIfNull(curl);

        var uri = new Uri(curl.Url);
        var credential = CredentialName(uri.Host);
        var notes = new List<string>();
        var secrets = new List<DraftSecret>();

        if (curl.User is { } user)
            secrets.Add(new DraftSecret(new HttpRecipeAuth { Type = AuthType.Basic, Credential = credential }, user, "-u"));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<string>();
        foreach (var (name, value) in curl.Headers)
        {
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                secrets.Add(Authorization(value, credential));
            else if (LooksSecret(name))
                secrets.Add(new DraftSecret(new HttpRecipeAuth { Type = AuthType.ApiKey, Credential = credential, In = ApiKeyLocation.Header, Name = name }, value, $"the {name} header"));
            else if (Noise.Contains(name) || name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase))
                dropped.Add(name);
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && value.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                continue;
            else
                headers[name] = value;
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in Query(uri.Query))
        {
            if (SecretParameters.Contains(name))
                secrets.Add(new DraftSecret(new HttpRecipeAuth { Type = AuthType.ApiKey, Credential = credential, In = ApiKeyLocation.Query, Name = name }, value, $"the {name} query parameter"));
            else
                query[name] = value;
        }

        JsonElement? body = null;
        if (curl.Body is { } text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                notes.Add("The body is not JSON, and recipes send JSON bodies only: it was left out.");
            }
        }

        if (dropped.Count > 0)
            notes.Add($"Browser headers left out: {string.Join(", ", dropped.Distinct(StringComparer.OrdinalIgnoreCase))}.");

        if (curl.Ignored.Count > 0)
            notes.Add($"Options not used: {string.Join(", ", curl.Ignored)}.");

        var secret = secrets.FirstOrDefault();
        if (secret is not null)
            notes.Add($"The credential in {secret.Origin} is not in the recipe. The recipe names it '{credential}': give its value in your ICredentialStore, or when you run 'synclane test'.");

        if (secrets.Count > 1)
            notes.Add($"The request carries more credentials ({string.Join(", ", secrets.Skip(1).Select(other => other.Origin))}). A recipe uses one; an API that needs several is called with a signer (IRequestSigner).");

        var recipe = new HttpRecipe
        {
            Schema = "https://raw.githubusercontent.com/ferhatblnk/synclane/main/schemas/recipe.schema.json",
            Name = $"{credential}/{Resource(uri)}",
            Request = new HttpRecipeRequest
            {
                Method = curl.Method,
                Url = uri.GetLeftPart(UriPartial.Path),
                Query = query,
                Headers = headers,
                Body = body,
                AllowHttp = uri.Scheme == Uri.UriSchemeHttp
            },
            Auth = secret?.Auth
        };

        return new RecipeDraft(recipe, notes, secret);
    }

    private static DraftSecret Authorization(string value, string credential)
    {
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        var scheme = space > 0 ? value[..space] : "";
        var token = space > 0 ? value[(space + 1)..].Trim() : value;

        if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            return new DraftSecret(new HttpRecipeAuth { Type = AuthType.Bearer, Credential = credential }, token, "the Authorization header");

        if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = TryBase64(token) ?? token;
            return new DraftSecret(new HttpRecipeAuth { Type = AuthType.Basic, Credential = credential }, decoded, "the Authorization header");
        }

        // Any other scheme, such as "Token abc": the same header, with the scheme as its prefix.
        return new DraftSecret(
            new HttpRecipeAuth { Type = AuthType.ApiKey, Credential = credential, In = ApiKeyLocation.Header, Name = "Authorization", Prefix = scheme.Length > 0 ? $"{scheme} " : null },
            token, "the Authorization header");
    }

    private static bool LooksSecret(string header)
    {
        if (NotSecret.Contains(header))
            return false;

        var name = header.ToLowerInvariant();
        return name.Contains("token", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal)
            || name.Contains("api-key", StringComparison.Ordinal) || name.Contains("apikey", StringComparison.Ordinal)
            || name.Contains("api_key", StringComparison.Ordinal) || name.EndsWith("-key", StringComparison.Ordinal)
            || name.Contains("password", StringComparison.Ordinal) || name.StartsWith("x-auth", StringComparison.Ordinal);
    }

    /// <summary>
    /// The name a credential is given: the site's name, such as <c>example-shop</c> for api.example-shop.com and for
    /// api.example-shop.com.tr. An address has no name to give, so it is <c>api</c>.
    /// </summary>
    internal static string CredentialName(string host)
    {
        if (Uri.CheckHostName(host.Trim('[', ']')) is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return "api";

        var labels = host.ToLowerInvariant().Split('.');
        if (labels.Length < 2)
            return labels[0];

        // A country's second level (com.tr, co.uk) is not the site's name: the label before it is.
        var site = labels.Length >= 3 && labels[^1].Length == 2 && CountryLevels.Contains(labels[^2]) ? labels.Length - 3 : labels.Length - 2;
        return labels[site];
    }

    /// <summary>Second-level labels countries put before their code, as in example.com.tr or example.co.uk.</summary>
    private static readonly HashSet<string> CountryLevels = ["co", "com", "net", "org", "gov", "edu", "ac", "gen", "biz", "info", "web", "ltd", "plc", "or", "ne", "go"];

    /// <summary>The last segment of the path that is not a version or a number, such as <c>issues</c>.</summary>
    private static string Resource(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Path.GetFileNameWithoutExtension(segment))
            .Where(segment => segment.Length > 0 && !segment.All(char.IsAsciiDigit) && !(segment.Length <= 4 && segment[0] is 'v' or 'V' && segment[1..].All(char.IsAsciiDigit)))
            .ToList();

        return segments.Count > 0 ? segments[^1].ToLowerInvariant() : "records";
    }

    private static IEnumerable<KeyValuePair<string, string>> Query(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            var value = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            return new KeyValuePair<string, string>(name, value);
        });

    private static string? TryBase64(string text)
    {
        var bytes = new byte[text.Length];
        return Convert.TryFromBase64String(text, bytes, out var written) ? Encoding.UTF8.GetString(bytes, 0, written) : null;
    }
}

/// <summary>
/// The credential a curl carried: how the recipe will ask for it, its value for the one request that checks the draft,
/// and where it was found. Never written anywhere; <see cref="ToString"/> leaves the value out.
/// </summary>
internal sealed record DraftSecret(HttpRecipeAuth Auth, string Value, string Origin)
{
    public override string ToString() => $"Credential from {Origin}";
}
