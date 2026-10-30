using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>Checks a recipe's pagination and builds its paginator. Every problem is added to the recipe's list of errors.</summary>
internal static partial class PaginationCompiler
{
    public static IPaginator? Compile(
        string recipe,
        HttpRecipePagination? pagination,
        Origin origin,
        IReadOnlyDictionary<string, string> query,
        string? secretParam,
        List<SyncError> errors)
    {
        if (pagination is null)
            return SinglePage.Instance;

        var before = errors.Count;
        var type = Name(pagination.Type);

        void Fail(string field, string rule) => errors.Add(HttpErrors.PaginationInvalid(recipe, field, rule));

        void Required(string field, object? value)
        {
            if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
                Fail(field, $"is required for type {type}");
        }

        void NotFor(string field, object? value)
        {
            if (value is not null)
                Fail(field, $"does not apply to type {type}");
        }

        RecordPath? Path(string field, string? text)
        {
            if (text is null)
                return null;

            var path = RecordPath.Parse(text);
            if (path is null)
                Fail(field, "is not a supported path; use $.name.name");

            return path;
        }

        if (!Enum.IsDefined(pagination.Type))
        {
            Fail("type", "is not one of page, offset, cursor, nextUrl or linkHeader");
            return null;
        }

        if (pagination.Start < 0)
            Fail("start", "must be zero or more");

        if (pagination.Size < 1)
            Fail("size", "must be at least 1");

        if (pagination.SizeParam is not null && pagination.Size is null)
            Fail("sizeParam", "needs size, the value to send");

        foreach (var (field, name) in new[] { ("param", pagination.Param), ("sizeParam", pagination.SizeParam) })
        {
            if (name is not null && query.ContainsKey(name))
                Fail(field, $"'{name}' is also set in request.query; set it in one place");
        }

        if (pagination.FromLastRecord is not null && !PropertyName().IsMatch(pagination.FromLastRecord))
            Fail("fromLastRecord", "must be a property name: letters, digits, underscores and hyphens");

        var from = Path("from", pagination.From);
        var hasMore = Path("hasMore", pagination.HasMore);

        switch (pagination.Type)
        {
            case PaginationType.Page or PaginationType.Offset:
                Required("param", pagination.Param);
                if (pagination.Type == PaginationType.Offset)
                    Required("size", pagination.Size);

                NotFor("from", pagination.From);
                NotFor("fromLastRecord", pagination.FromLastRecord);
                NotFor("hasMore", pagination.HasMore);
                break;

            case PaginationType.Cursor:
                Required("param", pagination.Param);
                NotFor("start", pagination.Start);
                if ((pagination.From is null) == (pagination.FromLastRecord is null))
                    Fail("from", "or fromLastRecord is required, and only one of them");

                break;

            case PaginationType.NextUrl or PaginationType.LinkHeader:
                if (pagination.Type == PaginationType.NextUrl)
                    Required("from", pagination.From);
                else
                    NotFor("from", pagination.From);

                NotFor("param", pagination.Param);
                NotFor("start", pagination.Start);
                NotFor("size", pagination.Size);
                NotFor("sizeParam", pagination.SizeParam);
                NotFor("fromLastRecord", pagination.FromLastRecord);
                NotFor("hasMore", pagination.HasMore);
                break;
        }

        if (errors.Count > before)
            return null;

        return pagination.Type switch
        {
            PaginationType.Page => new NumberedPagination(recipe, false, pagination.Param!, pagination.Start ?? 1, pagination.Size, pagination.SizeParam),
            PaginationType.Offset => new NumberedPagination(recipe, true, pagination.Param!, pagination.Start ?? 0, pagination.Size, pagination.SizeParam),
            PaginationType.Cursor => new CursorPagination(recipe, pagination.Param!, from, pagination.FromLastRecord, hasMore, pagination.Size, pagination.SizeParam),
            _ => new UrlPagination(recipe, origin, from, secretParam)
        };
    }

    private static string Name(PaginationType type) => type switch
    {
        PaginationType.Page => "page",
        PaginationType.Offset => "offset",
        PaginationType.Cursor => "cursor",
        PaginationType.NextUrl => "nextUrl",
        PaginationType.LinkHeader => "linkHeader",
        _ => type.ToString()
    };

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex PropertyName();
}
