using System.Net;

namespace SyncLane.Http;

/// <summary>Names and defaults of the HTTP source.</summary>
public static class HttpSource
{
    /// <summary>
    /// The name of the <see cref="HttpClient"/> recipes use. Add your own handlers to it with
    /// <c>services.AddHttpClient(HttpSource.ClientName)</c>, for a proxy for example. Do not add retry handlers: the engine
    /// already retries the job, from its cursor, with a growing delay.
    /// </summary>
    public const string ClientName = "SyncLane.Http";

    /// <summary>
    /// The connection handler: no redirects, so a recipe only calls the host it names and headers never travel to
    /// another one; gzip, deflate and brotli answered when the server offers them; connections renewed every two
    /// minutes so DNS changes are seen.
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };
}
