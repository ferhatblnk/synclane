using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// One way of paging. The job's cursor is the paginator's whole state, so a job that stops continues from the page it
/// reached: the paginator reads the cursor to build the next request and writes the next cursor from the answer.
/// </summary>
internal interface IPaginator
{
    /// <summary>What the request for <paramref name="cursor"/> needs. <c>null</c> is the first page.</summary>
    SyncResult<PageRequest> Request(string? cursor, int maxPages);

    /// <summary>The cursor of the page after this one, or <c>null</c> when this was the last page.</summary>
    SyncResult<string?> Next(string? cursor, PageAnswer answer);
}

/// <summary>What a request needs from the pagination: query parameters to add, or a whole URL to call instead.</summary>
internal readonly record struct PageRequest(IReadOnlyList<KeyValuePair<string, string>> Query, Uri? Url)
{
    public static readonly PageRequest None = new([], null);
}

/// <summary>What the pagination may look at in an answer.</summary>
internal readonly record struct PageAnswer(JsonElement Root, JsonElement Records, IReadOnlyList<string> LinkHeaders, Uri RequestUri);

/// <summary>
/// Scheme, host and port: where a recipe is allowed to send requests. With <see cref="Hosts"/>, the host is any of them:
/// a name, or <c>*.</c> and a domain for one name under it.
/// </summary>
internal sealed record Origin(string Scheme, string Host, int Port, IReadOnlyList<string>? Hosts = null)
{
    public static Origin Of(Uri uri) => new(uri.Scheme, uri.Host, uri.Port);

    public bool Matches(Uri uri) =>
        uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.Port == Port
        && (Hosts is null ? string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) : Hosts.Any(pattern => Allows(pattern, uri.Host)));

    private static bool Allows(string pattern, string host)
    {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
            return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);

        var domain = pattern[1..];
        return host.Length > domain.Length
            && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
            && !host.AsSpan(0, host.Length - domain.Length).Contains('.');
    }
}

/// <summary>No paging: one request returns everything.</summary>
internal sealed class SinglePage : IPaginator
{
    public static readonly SinglePage Instance = new();

    public SyncResult<PageRequest> Request(string? cursor, int maxPages) => SyncResult<PageRequest>.Success(PageRequest.None);

    public SyncResult<string?> Next(string? cursor, PageAnswer answer) => SyncResult<string?>.Success(null);
}
