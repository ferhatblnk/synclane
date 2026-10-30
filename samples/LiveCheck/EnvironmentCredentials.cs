using SyncLane.Http;

namespace LiveCheck;

/// <summary>
/// The credential store of the live check: one token, read from an environment variable. A real application reads
/// each owner's credential from its own encrypted storage.
/// </summary>
internal sealed class EnvironmentCredentials : ICredentialStore
{
    public const string Variable = "SYNCLANE_LIVE_GITHUB_TOKEN";

    public static string? Token => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } token ? token : null;

    public ValueTask<Credential?> GetAsync(CredentialKey key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(key.Name == "github" && Token is { } token ? new Credential { Token = token } : null);

    // No recipe of the live check uses a refresh token, so nothing is ever rotated.
    public ValueTask SaveRefreshTokenAsync(CredentialKey key, string refreshToken, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
