namespace SyncLane.Http;

/// <summary>
/// One authentication scheme of a compiled recipe: adds the owner's credential to a rendered request. Values are
/// checked when used, and a problem abandons the job with a message that names the value, never shows it.
/// </summary>
internal abstract class RecipeAuth(string recipe, string credential)
{
    public string Recipe => recipe;

    /// <summary>The credential name the store is asked for.</summary>
    public string Credential => credential;

    /// <summary>The query parameter that carries a secret, which is taken out of every URL that is saved.</summary>
    public virtual string? SecretParam => null;

    /// <summary>Whether a token rejected with 401 can be renewed and the request sent once more.</summary>
    public virtual bool Renews => false;

    /// <summary>
    /// Adds the credential to <paramref name="request"/>. <paramref name="rejected"/> is a token the API just answered
    /// 401 to, which must not be used again.
    /// </summary>
    public abstract ValueTask<AuthStamp> ApplyAsync(
        HttpRequestMessage request, AuthContext context, string? rejected, CancellationToken cancellationToken);

    /// <summary>The value, or an abandoned job when the credential does not have it.</summary>
    public string Required(string? value, string field) =>
        string.IsNullOrEmpty(value) ? throw Invalid($"has no {field}") : value;

    /// <summary>The value, or an abandoned job when it holds a character a header cannot carry.</summary>
    public string ForHeader(string value, string field)
    {
        foreach (var character in value)
        {
            if (character is < ' ' or > '~')
                throw Invalid($"has a {field} with a character a header cannot carry, such as a line break or a non-ASCII letter");
        }

        return value;
    }

    public JobAbandonedException Invalid(string problem) =>
        new(HttpErrors.CredentialInvalid(recipe, credential, problem).ToString());
}

/// <summary>What a page's requests are authenticated with. Built once per page.</summary>
internal sealed class AuthContext(CredentialKey key, Credential credential, ICredentialStore store, IServiceProvider services)
{
    public CredentialKey Key => key;

    public Credential Credential => credential;

    public ICredentialStore Store => store;

    public IServiceProvider Services => services;
}

/// <summary>The token a request was sent with, when it can be renewed, and whether it was fetched for this request.</summary>
internal readonly struct AuthStamp(string? token, bool fresh)
{
    public static readonly AuthStamp None = default;

    public string? Token => token;

    public bool Fresh => fresh;
}
