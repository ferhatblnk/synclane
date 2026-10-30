using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>Checks a recipe's authentication and builds its scheme. Every problem is added to the recipe's list of errors.</summary>
internal static partial class AuthCompiler
{
    /// <summary>The scheme, or <c>null</c> when the recipe has no auth or the auth has errors.</summary>
    public static RecipeAuth? Compile(string recipe, HttpRecipe source, IReadOnlySet<string> reservedHeaders, List<SyncError> errors)
    {
        if (source.Auth is not { } auth)
            return null;

        var before = errors.Count;
        var request = source.Request;
        var type = Name(auth.Type);

        void Fail(string field, string rule) => errors.Add(HttpErrors.AuthInvalid(recipe, field, rule));

        void Required(string field, object? value)
        {
            if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
                Fail(field, $"is required for type {type}");
        }

        void NotFor(string field, object? value)
        {
            if (value is not null)
                Fail(field, $"does not apply to type {type}");
        }

        if (!Enum.IsDefined(auth.Type))
        {
            Fail("type", "is not one of bearer, basic, apiKey, oauth2 or signed");
            return null;
        }

        // Null when set to null in JSON or code; a missing field is refused by the JSON reader.
        var credential = auth.Credential ?? "";
        if (!Identifier().IsMatch(credential))
            Fail("credential", "must be 1 to 200 letters, digits, dots, underscores, colons, slashes or hyphens");

        if (auth.Type != AuthType.ApiKey)
        {
            NotFor("in", auth.In);
            NotFor("name", auth.Name);
            NotFor("prefix", auth.Prefix);
        }

        if (auth.Type != AuthType.OAuth2)
        {
            NotFor("grant", auth.Grant);
            NotFor("tokenUrl", auth.TokenUrl);
            NotFor("scope", auth.Scope);
            NotFor("clientAuth", auth.ClientAuth);
        }

        if (auth.Type != AuthType.Signed)
            NotFor("signer", auth.Signer);

        Uri? tokenUrl = null;
        switch (auth.Type)
        {
            case AuthType.ApiKey:
                CheckApiKey(auth, source, reservedHeaders, Required, Fail);
                break;

            case AuthType.OAuth2:
                Required("grant", auth.Grant);
                if (auth.Grant is { } grant && !Enum.IsDefined(grant))
                    Fail("grant", "is not one of clientCredentials or refreshToken");

                if (auth.ClientAuth is { } clientAuth && !Enum.IsDefined(clientAuth))
                    Fail("clientAuth", "is not one of basic or body");

                if (auth.Scope is { } scope && (string.IsNullOrWhiteSpace(scope) || scope.AsSpan().ContainsAnyExceptInRange(' ', '~')))
                    Fail("scope", "must be printable ASCII on one line, scopes separated by spaces");

                Required("tokenUrl", auth.TokenUrl);
                tokenUrl = CheckTokenUrl(auth.TokenUrl, request.AllowHttp, Fail);
                break;

            case AuthType.Signed:
                Required("signer", auth.Signer);
                if (auth.Signer is { } signer && !Identifier().IsMatch(signer))
                    Fail("signer", "must be 1 to 200 letters, digits, dots, underscores, colons, slashes or hyphens");

                break;
        }

        if (errors.Count > before)
            return null;

        return auth.Type switch
        {
            AuthType.Bearer => new BearerAuth(recipe, credential),
            AuthType.Basic => new BasicAuth(recipe, credential),
            AuthType.ApiKey => new ApiKeyAuth(recipe, credential, auth.In ?? ApiKeyLocation.Header, auth.Name!, auth.Prefix ?? ""),
            AuthType.OAuth2 => new OAuth2Auth(recipe, credential, auth.Grant!.Value, tokenUrl!, auth.Scope, auth.ClientAuth ?? OAuth2ClientAuth.Basic),
            _ => new SignedAuth(recipe, credential, auth.Signer!)
        };
    }

    private static void CheckApiKey(
        HttpRecipeAuth auth, HttpRecipe source, IReadOnlySet<string> reservedHeaders, Action<string, object?> required, Action<string, string> fail)
    {
        required("name", auth.Name);
        if (auth.In is { } location && !Enum.IsDefined(location))
            fail("in", "is not one of header or query");

        if (string.IsNullOrWhiteSpace(auth.Name))
            return;

        var name = auth.Name;
        if (auth.In == ApiKeyLocation.Query)
        {
            if (auth.Prefix is not null)
                fail("prefix", "applies only to a key in a header");

            var pagination = source.Pagination;
            if (source.Request.Query.ContainsKey(name))
                fail("name", $"'{name}' is also set in request.query; set it in one place");
            else if (name == pagination?.Param || name == pagination?.SizeParam)
                fail("name", $"'{name}' is also a pagination parameter");

            return;
        }

        if (!HttpToken().IsMatch(name) || reservedHeaders.Contains(name) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            fail("name", $"'{name}' is not a header SyncLane can set");
        else if (source.Request.Headers.Keys.Contains(name, StringComparer.OrdinalIgnoreCase))
            fail("name", $"'{name}' is also set in request.headers; set it in one place");

        if (auth.Prefix is { } prefix && prefix.AsSpan().ContainsAnyExceptInRange(' ', '~'))
            fail("prefix", "must be printable ASCII on one line");
    }

    private static Uri? CheckTokenUrl(string? text, bool allowHttp, Action<string, string> fail)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Contains("{{", StringComparison.Ordinal))
            fail("tokenUrl", "must not contain placeholders; it is the same for every owner");
        else if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host))
            fail("tokenUrl", "is not an absolute URL");
        else if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && allowHttp))
            fail("tokenUrl", "must use https");
        else if (!string.IsNullOrEmpty(url.UserInfo))
            fail("tokenUrl", "must not contain a user name or password; the credential store gives the client's");
        else
            return url;

        return null;
    }

    private static string Name(AuthType type) => type switch
    {
        AuthType.Bearer => "bearer",
        AuthType.Basic => "basic",
        AuthType.ApiKey => "apiKey",
        AuthType.OAuth2 => "oauth2",
        AuthType.Signed => "signed",
        _ => type.ToString()
    };

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,200}$")]
    private static partial Regex Identifier();

    /// <summary>An HTTP token as RFC 9110 defines it.</summary>
    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$")]
    private static partial Regex HttpToken();
}
