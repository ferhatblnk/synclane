using Microsoft.Extensions.DependencyInjection;

namespace SyncLane.Http;

/// <summary><c>Authorization: Bearer</c> with an OAuth 2.0 access token from the recipe's token endpoint.</summary>
internal sealed class OAuth2Auth(
    string recipe, string credential, OAuth2Grant grant, Uri tokenUrl, string? scope, OAuth2ClientAuth clientAuth)
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
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + stamp.Token);
        return stamp;
    }
}
