using System.Globalization;
using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// A token from the answer, sent back in a query parameter: from a path in the answer, or from a property of the last
/// record. The last page is the one without a token, or with the has-more flag set to <c>false</c>.
/// </summary>
internal sealed class CursorPagination(
    string recipe, string param, RecordPath? from, string? fromLastRecord, RecordPath? hasMore, int? size, string? sizeParam) : IPaginator
{
    public SyncResult<PageRequest> Request(string? cursor, int maxPages)
    {
        List<KeyValuePair<string, string>> query = [];
        if (cursor is not null)
            query.Add(new(param, cursor));

        if (sizeParam is not null)
            query.Add(new(sizeParam, size!.Value.ToString(CultureInfo.InvariantCulture)));

        return SyncResult<PageRequest>.Success(new PageRequest(query, null));
    }

    public SyncResult<string?> Next(string? cursor, PageAnswer answer)
    {
        if (hasMore is not null && hasMore.TryFind(answer.Root, out var flag) && flag.ValueKind != JsonValueKind.Null)
        {
            if (flag.ValueKind == JsonValueKind.False)
                return SyncResult<string?>.Success(null);

            if (flag.ValueKind != JsonValueKind.True)
                return SyncResult<string?>.Failure(HttpErrors.CursorValueInvalid(recipe, "hasMore", "true or false"));
        }

        var found = Find(answer, out var value);
        if (!found || value.ValueKind == JsonValueKind.Null)
            return SyncResult<string?>.Success(null);

        var next = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        if (next is null && value.ValueKind is not JsonValueKind.String)
            return SyncResult<string?>.Failure(HttpErrors.CursorValueInvalid(recipe, from is null ? "fromLastRecord" : "from", "a string or a number"));

        if (string.IsNullOrEmpty(next))
            return SyncResult<string?>.Success(null);

        return next == cursor
            ? SyncResult<string?>.Failure(HttpErrors.PageRepeated(recipe))
            : SyncResult<string?>.Success(next);
    }

    private bool Find(PageAnswer answer, out JsonElement value)
    {
        if (from is not null)
            return from.TryFind(answer.Root, out value);

        value = default;
        var count = answer.Records.GetArrayLength();
        return count > 0
            && answer.Records[count - 1] is { ValueKind: JsonValueKind.Object } last
            && last.TryGetProperty(fromLastRecord!, out value);
    }
}
