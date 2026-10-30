using System.Globalization;

namespace SyncLane.Http;

/// <summary>
/// A page number or a record offset in a query parameter. The last page is the first one shorter than the page size,
/// or, without a size, the first empty one.
/// </summary>
internal sealed class NumberedPagination(
    string recipe, bool isOffset, string param, long start, int? size, string? sizeParam) : IPaginator
{
    public SyncResult<PageRequest> Request(string? cursor, int maxPages)
    {
        if (!TryRead(cursor, out var current))
            return SyncResult<PageRequest>.Failure(HttpErrors.CursorUnreadable(recipe, isOffset ? "offset" : "page"));

        var pagesRead = isOffset ? (current - start) / size!.Value : current - start;
        if (pagesRead >= maxPages)
            return SyncResult<PageRequest>.Failure(HttpErrors.TooManyPages(recipe, maxPages));

        List<KeyValuePair<string, string>> query = [new(param, current.ToString(CultureInfo.InvariantCulture))];
        if (sizeParam is not null)
            query.Add(new(sizeParam, size!.Value.ToString(CultureInfo.InvariantCulture)));

        return SyncResult<PageRequest>.Success(new PageRequest(query, null));
    }

    public SyncResult<string?> Next(string? cursor, PageAnswer answer)
    {
        var count = answer.Records.GetArrayLength();
        var isLast = size is { } full ? count < full : count == 0;
        if (isLast || !TryRead(cursor, out var current))
            return SyncResult<string?>.Success(null);

        var next = isOffset ? current + count : current + 1;
        return SyncResult<string?>.Success(next.ToString(CultureInfo.InvariantCulture));
    }

    private bool TryRead(string? cursor, out long current)
    {
        if (cursor is null)
        {
            current = start;
            return true;
        }

        return long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out current) && current >= start;
    }
}
