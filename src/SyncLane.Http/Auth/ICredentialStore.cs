namespace SyncLane.Http;

/// <summary>
/// Gives SyncLane an owner's credential when a request needs it. Credentials stay where you keep them, encrypted as
/// you see fit; SyncLane never writes them to the jobs table, a log, a span or an error message. Register it with
/// <c>http.Credentials&lt;TStore&gt;()</c>; it is resolved from the scope of each page.
/// </summary>
/// <remarks>
/// <see cref="GetAsync"/> is called once for every page, so cache inside the store if a lookup is slow or rate limited.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>
    /// The credential for <paramref name="key"/>, or <c>null</c> when the owner has none, for example after they
    /// disconnected. <c>null</c> abandons the job with <c>SL5119</c>; an exception, such as an unreachable vault,
    /// retries it.
    /// </summary>
    ValueTask<Credential?> GetAsync(CredentialKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a refresh token the token endpoint issued in place of the one sent. Called only by <c>oauth2</c> recipes with
    /// the refresh token grant, after the endpoint rotated the token. The token passed is never cancelled: the old
    /// refresh token may already be retired, so losing the new one would disconnect the owner.
    /// </summary>
    ValueTask SaveRefreshTokenAsync(CredentialKey key, string refreshToken, CancellationToken cancellationToken);
}
