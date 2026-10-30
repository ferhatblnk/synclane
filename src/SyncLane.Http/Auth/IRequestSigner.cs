namespace SyncLane.Http;

/// <summary>
/// Signs requests for APIs that want a signature, such as a hash of the method, path, time and body. Register it by
/// name with <c>http.Signer&lt;TSigner&gt;("name")</c> and name it in the recipe's <c>auth.signer</c>; it is resolved from
/// the scope of each page.
/// </summary>
public interface IRequestSigner
{
    /// <summary>
    /// Adds the signature to <paramref name="request"/>, in headers or query parameters. The request is complete: the URL
    /// with its paging parameters, the headers and the body. The method, scheme, host and port must stay as they are;
    /// changing them abandons the job with <c>SL5124</c>, so a signer cannot send the request somewhere else.
    /// </summary>
    ValueTask SignAsync(HttpRequestMessage request, Credential credential, CancellationToken cancellationToken);
}
