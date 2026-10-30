using Microsoft.Extensions.DependencyInjection;

namespace SyncLane.Http;

/// <summary>Each request signed by the <see cref="IRequestSigner"/> registered under the recipe's signer name.</summary>
internal sealed class SignedAuth(string recipe, string credential, string signer) : RecipeAuth(recipe, credential)
{
    public string Signer => signer;

    public override async ValueTask<AuthStamp> ApplyAsync(
        HttpRequestMessage request, AuthContext context, string? rejected, CancellationToken cancellationToken)
    {
        var method = request.Method;
        var origin = Origin.Of(request.RequestUri!);

        await context.Services.GetRequiredKeyedService<IRequestSigner>(signer).SignAsync(request, context.Credential, cancellationToken);

        // A signer may add to the request but not send it somewhere else, so the recipe still names every host it calls.
        if (request.Method != method || request.RequestUri is not { } signed || !origin.Matches(signed))
            throw new JobAbandonedException(HttpErrors.SignerMovedRequest(Recipe, signer).ToString());

        return AuthStamp.None;
    }
}
