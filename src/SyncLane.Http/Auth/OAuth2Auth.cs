using Microsoft.Extensions.DependencyInjection;

namespace SyncLane.Http;

/// <summary>
/// An OAuth 2.0 access token from the recipe's token endpoint, in <c>Authorization: Bearer</c> or in the header the
/// recipe names, for APIs that want it elsewhere.
/// </summary>
internal sealed class OAuth2Auth(
    string recipe, string credential, OAuth2Grant grant, Uri tokenUrl, string? scope, OAuth2ClientAuth clientAuth, string header, string prefix)
    : RecipeAuth(recipe, credential)
{
    public OAuth2Grant Grant => grant;

    public Uri TokenUrl => tokenUrl;

    public string? Scope => scope;

    public OAuth2ClientAuth ClientAuth => clientAuth;

    public override bool Renews => true;

    public override async ValueTask<AuthStamp> ApplyAsync(
        HttpRequestMessage request, AuthContext context, string? rejected, CancellationToken cancellationToken)
    {
        var stamp = await context.Services.GetRequiredService<OAuthTokens>().GetAsync(this, context, rejected, cancellationToken);
        request.Headers.TryAddWithoutValidation(header, prefix + stamp.Token);
        return stamp;
    }
}
