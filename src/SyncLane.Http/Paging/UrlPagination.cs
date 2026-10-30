using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// The full URL of the next page: from a path in the answer, or from the <c>Link</c> header when <c>from</c> is not set.
/// A URL on another scheme, host or port is refused, so the recipe's headers never travel to a host it does not name.
/// </summary>
internal sealed class UrlPagination(string recipe, Origin origin, RecordPath? from) : IPaginator
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

        return next.AbsoluteUri == answer.RequestUri.AbsoluteUri
            ? SyncResult<string?>.Failure(HttpErrors.PageRepeated(recipe))
            : SyncResult<string?>.Success(next.AbsoluteUri);
    }
}
