using System.Text;

namespace SyncLane.Http;

/// <summary><c>Authorization: Bearer</c> with the credential's token.</summary>
internal sealed class BearerAuth(string recipe, string credential) : RecipeAuth(recipe, credential)
{
    public override ValueTask<AuthStamp> ApplyAsync(
        HttpRequestMessage request, AuthContext context, string? rejected, CancellationToken cancellationToken)
    {
        var token = ForHeader(Required(context.Credential.Token, nameof(Http.Credential.Token)), nameof(Http.Credential.Token));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return ValueTask.FromResult(AuthStamp.None);
    }
}

/// <summary><c>Authorization: Basic</c> with the credential's user name and password, encoded as UTF-8 (RFC 7617).</summary>
internal sealed class BasicAuth(string recipe, string credential) : RecipeAuth(recipe, credential)
{
    public override ValueTask<AuthStamp> ApplyAsync(
        HttpRequestMessage request, AuthContext context, string? rejected, CancellationToken cancellationToken)
    {
        var username = Required(context.Credential.Username, nameof(Http.Credential.Username));
        var password = context.Credential.Password ?? "";

        if (username.Contains(':', StringComparison.Ordinal))
            throw Invalid("has a Username with a colon, which Basic authentication cannot carry");

        if (username.AsSpan().ContainsAnyInRange('\0', '\u001f') || password.AsSpan().ContainsAnyInRange('\0', '\u001f'))
            throw Invalid("has a Username or Password with a control character, such as a line break");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        request.Headers.TryAddWithoutValidation("Authorization", "Basic " + encoded);
        return ValueTask.FromResult(AuthStamp.None);
    }
}

/// <summary>The credential's token in a header or query parameter the recipe names.</summary>
internal sealed class ApiKeyAuth(string recipe, string credential, ApiKeyLocation location, string name, string prefix)
    : RecipeAuth(recipe, credential)
{
    public override string? SecretParam => location == ApiKeyLocation.Query ? name : null;

    public override ValueTask<AuthStamp> ApplyAsync(
        HttpRequestMessage request, AuthContext context, string? rejected, CancellationToken cancellationToken)
    {
        var key = Required(context.Credential.Token, nameof(Http.Credential.Token));

        if (location == ApiKeyLocation.Query)
            request.RequestUri = QueryString.With(request.RequestUri!, name, key);
        else
            request.Headers.TryAddWithoutValidation(name, prefix + ForHeader(key, nameof(Http.Credential.Token)));

        return ValueTask.FromResult(AuthStamp.None);
    }
}
