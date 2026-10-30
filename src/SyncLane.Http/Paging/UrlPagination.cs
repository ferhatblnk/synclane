using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// The full URL of the next page: from a path in the answer, or from the <c>Link</c> header when <c>from</c> is not set.
/// A URL on another scheme, host or port is refused, so the recipe's headers never travel to a host it does not name.
/// </summary>
/// <remarks>
/// When an API key travels in the query, many APIs copy it into their next-page URLs. The key is taken out before the
/// URL becomes the job's cursor, so it never reaches the jobs table; it is added again when the page is requested.
/// </remarks>
internal sealed class UrlPagination(string recipe, Origin origin, RecordPath? from, string? secretParam) : IPaginator
{
    private string Field => from is null ? "type linkHeader" : "from";

    public SyncResult<PageRequest> Request(string? cursor, int maxPages)
    {
        if (cursor is null)
            return SyncResult<PageRequest>.Success(PageRequest.None);

        if (!Uri.TryCreate(cursor, UriKind.Absolute, out var url))
            return SyncResult<PageRequest>.Failure(HttpErrors.CursorUnreadable(recipe, "URL"));

        return origin.Matches(url)
            ? SyncResult<PageRequest>.Success(new PageRequest([], url))
            : SyncResult<PageRequest>.Failure(HttpErrors.NextUrlForeignHost(recipe));
    }

    public SyncResult<string?> Next(string? cursor, PageAnswer answer)
    {
        string? text;
        if (from is null)
            text = LinkHeader.Next(answer.LinkHeaders);
        else if (!from.TryFind(answer.Root, out var value) || value.ValueKind == JsonValueKind.Null)
            text = null;
        else if (value.ValueKind == JsonValueKind.String)
            text = value.GetString();
        else
            return SyncResult<string?>.Failure(HttpErrors.CursorValueInvalid(recipe, Field, "a URL"));

        if (string.IsNullOrEmpty(text))
            return SyncResult<string?>.Success(null);

        // A relative URL is read against the request that returned it, as a browser would.
        if (!Uri.TryCreate(answer.RequestUri, text, out var next))
            return SyncResult<string?>.Failure(HttpErrors.CursorValueInvalid(recipe, Field, "a URL"));

        if (!origin.Matches(next))
            return SyncResult<string?>.Failure(HttpErrors.NextUrlForeignHost(recipe));

        var read = answer.RequestUri;
        if (secretParam is not null)
        {
            next = QueryString.Without(next, secretParam);
            read = QueryString.Without(read, secretParam);
        }

        return next.AbsoluteUri == read.AbsoluteUri
            ? SyncResult<string?>.Failure(HttpErrors.PageRepeated(recipe))
            : SyncResult<string?>.Success(next.AbsoluteUri);
    }
}
