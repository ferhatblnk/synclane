using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SyncLane.Http;

namespace SyncLane.Cli.Probe;

/// <summary>
/// What one real answer says about a recipe: where its records are, how it pages, which query values are its time
/// window, which headers say how many requests are left, and what could key its records. Every guess comes with the
/// reason, written next to the field it fills.
/// </summary>
internal static partial class Inference
{
    private static readonly string[] RecordNames = ["data", "items", "results", "records", "content", "entries", "features", "feed", "list", "values", "rows"];
    private static readonly string[] CursorFields = ["next_cursor", "nextcursor", "cursor", "next_page_token", "nextpagetoken", "next_token", "nexttoken", "continuation", "continuationtoken", "scroll_id", "after"];
    private static readonly string[] HasMoreFields = ["has_more", "hasmore", "has_next", "hasnext", "more"];
    private static readonly string[] PageParams = ["page", "page_number", "pagenumber", "pageno"];
    private static readonly string[] OffsetParams = ["offset", "skip", "start"];
    private static readonly string[] SizeParams = ["per_page", "page_size", "pagesize", "limit", "size", "count", "max_results", "maxresults"];
    private static readonly string[] CursorParams = ["cursor", "page_token", "pagetoken", "next_token", "after", "starting_after", "continuation"];
    private static readonly string[] EndHints = ["until", "before", "end", "to", "max", "lte", "lt"];
    private static readonly string[] StartHints = ["since", "after", "start", "from", "begin", "min", "gte", "gt", "updated", "created", "modified"];
    private static readonly string[] VersionFields = ["updated_at", "updatedat", "modified_at", "modifiedat", "last_modified", "lastmodified", "date_modified", "updated"];

    public static Inferred Infer(HttpRecipe draft, JsonElement answer, IReadOnlyDictionary<string, string> headers)
    {
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();

        var (records, recordArray) = Records(answer, comments, notes);
        var query = new Dictionary<string, string>(draft.Request.Query, StringComparer.Ordinal);
        var window = Window(query, comments, notes);
        var pagination = Pagination(answer, recordArray, headers, query, comments);
        var rate = Rate(draft, headers, comments, notes);
        Keys(recordArray, notes);

        if (pagination is null && recordArray is { } array && SizeParams.Select(name => Value(query, name)).OfType<string>().FirstOrDefault() is { } size
            && int.TryParse(size, CultureInfo.InvariantCulture, out var asked) && array.GetArrayLength() >= asked)
        {
            notes.Add($"The page is full ({asked} records) but no way to the next page was found in the answer: add a pagination section, or the job reads one page only.");
        }

        var recipe = draft with
        {
            Request = draft.Request with { Query = query },
            Records = records,
            Window = window,
            Pagination = pagination,
            Rate = rate
        };

        return new Inferred(recipe, comments, notes);
    }

    private static (string Path, JsonElement? Array) Records(JsonElement answer, Dictionary<string, string> comments, List<string> notes)
    {
        if (answer.ValueKind == JsonValueKind.Array)
        {
            comments["records"] = "the answer is the array of records";
            return ("$", answer);
        }

        var best = Arrays(answer, "$", 0)
            .OrderByDescending(found => RecordNames.Contains(found.Name, StringComparer.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(found => found.Array.GetArrayLength())
            .FirstOrDefault();

        if (best.Path is null)
        {
            notes.Add("The answer holds no array of records. If this endpoint returns one record, a recipe is not the right tool for it.");
            return ("$", null);
        }

        comments["records"] = $"guessed: the array of {best.Array.GetArrayLength()} objects at {best.Path}";
        return (best.Path, best.Array);
    }

    private static IEnumerable<(string Path, string Name, JsonElement Array)> Arrays(JsonElement element, string path, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 2)
            yield break;

        foreach (var property in element.EnumerateObject())
        {
            if (!RecordPathName().IsMatch(property.Name))
                continue;

            var child = $"{path}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object))
                yield return (child, property.Name, property.Value);
            else
                foreach (var nested in Arrays(property.Value, child, depth + 1))
                    yield return nested;
        }
    }

    private static HttpRecipeWindow? Window(Dictionary<string, string> query, Dictionary<string, string> comments, List<string> notes)
    {
        var dated = query.Select(pair => (pair.Key, pair.Value, Format: DateFormat(pair.Value))).Where(found => found.Format is not null).ToList();
        if (dated.Count == 0)
            return null;

        var ends = dated.Where(found => Hinted(found.Key, EndHints) && !Hinted(found.Key, ["start", "from", "since", "after", "begin", "min"])).Select(found => found.Key).ToHashSet();
        if (ends.Count == 0 && dated.Count >= 2)
            ends.Add(dated.OrderByDescending(found => found.Value, StringComparer.Ordinal).First().Key);

        foreach (var (name, value, _) in dated)
        {
            var end = ends.Contains(name);
            query[name] = end ? "{{window.end}}" : "{{window.start}}";
            comments[$"request.query.{name}"] = $"guessed: '{value}' looks like the {(end ? "end" : "start")} of a time range";
        }

        var formats = dated.Select(found => found.Format!.Value).Distinct().ToList();
        if (formats.Count > 1)
            notes.Add("The dates in the query are written in different formats; the recipe writes them all as the first one. Check the API accepts that.");

        return formats[0] == WindowFormat.Iso8601 ? null : new HttpRecipeWindow { Format = formats[0] };
    }

    private static HttpRecipePagination? Pagination(
        JsonElement answer, JsonElement? records, IReadOnlyDictionary<string, string> headers, Dictionary<string, string> query, Dictionary<string, string> comments)
    {
        if (headers.TryGetValue("Link", out var link) && link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
        {
            comments["pagination"] = "the answer's Link header has rel=\"next\"";
            return new HttpRecipePagination { Type = PaginationType.LinkHeader };
        }

        var fields = Fields(answer, "$", 0).ToList();
        var nextUrl = fields.FirstOrDefault(field => field.Name.Contains("next", StringComparison.OrdinalIgnoreCase)
            && field.Value.ValueKind == JsonValueKind.String && Uri.TryCreate(field.Value.GetString(), UriKind.Absolute, out var next) && next.Scheme.StartsWith("http", StringComparison.Ordinal));
        if (nextUrl.Path is not null)
        {
            comments["pagination"] = $"guessed: {nextUrl.Path} holds the URL of the next page";
            return new HttpRecipePagination { Type = PaginationType.NextUrl, From = nextUrl.Path };
        }

        var (size, sizeParam) = Size(query);
        var hasMore = fields.FirstOrDefault(field => HasMoreFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase) && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        var cursor = fields.FirstOrDefault(field => CursorFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase) && field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number);
        if (cursor.Path is not null)
        {
            var param = query.Keys.FirstOrDefault(name => CursorParams.Contains(name, StringComparer.OrdinalIgnoreCase)) ?? CursorParam(cursor.Name);
            comments["pagination"] = $"guessed: {cursor.Path} holds the next cursor, sent back as the '{param}' query parameter";
            query.Remove(param);
            return new HttpRecipePagination
            {
                Type = PaginationType.Cursor, Param = param, From = cursor.Path, Size = size, SizeParam = sizeParam, HasMore = hasMore.Path
            };
        }

        if (hasMore.Path is not null && records is { } array && array.GetArrayLength() > 0 && array[0].ValueKind == JsonValueKind.Object && array[0].TryGetProperty("id", out _))
        {
            var param = query.Keys.FirstOrDefault(name => CursorParams.Contains(name, StringComparer.OrdinalIgnoreCase)) ?? "starting_after";
            comments["pagination"] = $"guessed: {hasMore.Path} says more pages follow, and the next starts after the last record's id, sent as '{param}'. Check the parameter's name in the API's documentation";
            query.Remove(param);
            return new HttpRecipePagination { Type = PaginationType.Cursor, Param = param, FromLastRecord = "id", HasMore = hasMore.Path, Size = size, SizeParam = sizeParam };
        }

        if (query.Keys.FirstOrDefault(name => PageParams.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } page)
        {
            comments["pagination"] = $"the request already has a '{page}' parameter";
            var start = int.TryParse(query[page], CultureInfo.InvariantCulture, out var first) && first != 1 ? first : (int?)null;
            query.Remove(page);
            return new HttpRecipePagination { Type = PaginationType.Page, Param = page, Start = start, Size = size, SizeParam = sizeParam };
        }

        if (query.Keys.FirstOrDefault(name => OffsetParams.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } offset && size is not null)
        {
            comments["pagination"] = $"the request already has an '{offset}' parameter";
            var start = int.TryParse(query[offset], CultureInfo.InvariantCulture, out var first) && first != 0 ? first : (int?)null;
            query.Remove(offset);
            return new HttpRecipePagination { Type = PaginationType.Offset, Param = offset, Start = start, Size = size, SizeParam = sizeParam };
        }

        return null;
    }

    /// <summary>The page size the request already asks for; the parameter moves into the pagination section.</summary>
    private static (int? Size, string? Param) Size(Dictionary<string, string> query)
    {
        foreach (var name in query.Keys.ToList())
        {
            if (SizeParams.Contains(name, StringComparer.OrdinalIgnoreCase) && int.TryParse(query[name], CultureInfo.InvariantCulture, out var size) && size > 0)
            {
                query.Remove(name);
                return (size, name);
            }
        }

        return (null, null);
    }

    private static HttpRecipeRate? Rate(HttpRecipe draft, IReadOnlyDictionary<string, string> headers, Dictionary<string, string> comments, List<string> notes)
    {
        string? Header(Regex pattern) => headers.Keys.FirstOrDefault(name => pattern.IsMatch(name));

        HttpRecipeRateHeaders? read = null;
        if (headers.ContainsKey("RateLimit"))
        {
            read = new HttpRecipeRateHeaders { Remaining = "RateLimit", RemainingFormat = RateRemainingFormat.Ietf };
        }
        else if (Header(RemainingHeader()) is { } remaining)
        {
            var reset = Header(ResetHeader());
            read = new HttpRecipeRateHeaders
            {
                Remaining = remaining,
                Reset = reset,
                ResetFormat = reset is null ? null : ResetFormat(headers[reset])
            };
        }
        else if (Header(TokenCountHeader()) is { } tokens)
        {
            read = new HttpRecipeRateHeaders { Remaining = tokens };
        }
        else if (headers.FirstOrDefault(pair => pair.Key.Contains("call-limit", StringComparison.OrdinalIgnoreCase) && UsedOfLimit().IsMatch(pair.Value)).Key is { } used)
        {
            read = new HttpRecipeRateHeaders { Remaining = used, RemainingFormat = RateRemainingFormat.UsedOfLimit };
        }

        if (Header(LimitHeader()) is { } limit)
            notes.Add($"The API reports its limit in {limit}: {headers[limit]}. Use it to choose the budget's limit.");

        if (read is null)
            return null;

        var budget = draft.Name.Split('/')[0];
        comments["rate"] = $"the answer says how many requests are left in {read.Remaining}";
        notes.Add($"The recipe draws from a rate budget named '{budget}'. Declare it in AddSyncLane: lane.AddBudget(\"{budget}\", limit: ..., per: ...).");
        return new HttpRecipeRate { Budget = budget, Headers = read };
    }

    private static void Keys(JsonElement? records, List<string> notes)
    {
        if (records is not { } array || array.GetArrayLength() == 0)
            return;

        var keyed = RecordIdentity.Inferred.Identify(array, "probe");
        if (keyed.Succeeded)
        {
            notes.Add("Every record has a unique id: destinations key records by $.id without being told.");
        }
        else
        {
            // The catalog's message already lists the fields that could serve; keep that sentence.
            var message = keyed.Errors[0].Message;
            var start = message.IndexOf("Fields that could serve", StringComparison.Ordinal);
            var candidates = start < 0 ? "" : " " + message[start..(message.IndexOf(". Name one", start, StringComparison.Ordinal) + 1)];
            notes.Add($"No unique id in every record.{candidates} Name the key in the destination with KeyedBy(...).");
        }

        if (array[0].ValueKind == JsonValueKind.Object
            && array[0].EnumerateObject().Select(property => property.Name).FirstOrDefault(name => VersionFields.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } version)
        {
            notes.Add($"Records carry $.{version}: give it to the destination with VersionedBy(\"$.{version}\") so an older copy never replaces a newer one.");
        }
    }

    private static IEnumerable<(string Path, string Name, JsonElement Value)> Fields(JsonElement element, string path, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 2)
            yield break;

        foreach (var property in element.EnumerateObject())
        {
            if (!RecordPathName().IsMatch(property.Name))
                continue;

            var child = $"{path}.{property.Name}";
            yield return (child, property.Name, property.Value);
            foreach (var nested in Fields(property.Value, child, depth + 1))
                yield return nested;
        }
    }

    /// <summary>The query parameter a cursor field is usually sent back as: <c>next_cursor</c> as <c>cursor</c>, <c>nextPageToken</c> as <c>pageToken</c>.</summary>
    private static string CursorParam(string field)
    {
        var name = Regex.Replace(field, "^next_?", "", RegexOptions.IgnoreCase);
        return name.Length == 0 ? "cursor" : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static WindowFormat? DateFormat(string value)
    {
        if (IsoDateTime().IsMatch(value))
            return WindowFormat.Iso8601;

        if (IsoDate().IsMatch(value))
            return WindowFormat.Date;

        if (long.TryParse(value, CultureInfo.InvariantCulture, out var number))
        {
            if (number is >= 946_684_800 and <= 4_102_444_800)
                return WindowFormat.Unix;

            if (number is >= 946_684_800_000 and <= 4_102_444_800_000)
                return WindowFormat.UnixMilliseconds;
        }

        return null;
    }

    private static RateResetFormat ResetFormat(string value) =>
        long.TryParse(value, CultureInfo.InvariantCulture, out var number)
            ? number >= 946_684_800_000 ? RateResetFormat.UnixMilliseconds : number >= 946_684_800 ? RateResetFormat.Unix : RateResetFormat.Seconds
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) ? RateResetFormat.HttpDate : RateResetFormat.Seconds;

    private static bool Hinted(string name, string[] hints)
    {
        var words = Words().Split(name.ToLowerInvariant()).Where(word => word.Length > 0).ToList();
        return hints.Any(hint => words.Contains(hint) || words.Any(word => word.StartsWith(hint, StringComparison.Ordinal) || word.EndsWith(hint, StringComparison.Ordinal)));
    }

    private static string? Value(Dictionary<string, string> query, string name) =>
        query.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex RecordPathName();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}")]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"[^a-z]+|(?<=[a-z])(?=[A-Z])")]
    private static partial Regex Words();

    [GeneratedRegex(@"^(x-)?rate-?limit-remaining$", RegexOptions.IgnoreCase)]
    private static partial Regex RemainingHeader();

    [GeneratedRegex(@"^(x-)?rate-?limit-reset$", RegexOptions.IgnoreCase)]
    private static partial Regex ResetHeader();

    [GeneratedRegex(@"^(x-)?rate-?limit-limit$", RegexOptions.IgnoreCase)]
    private static partial Regex LimitHeader();

    [GeneratedRegex(@"token-count$", RegexOptions.IgnoreCase)]
    private static partial Regex TokenCountHeader();

    [GeneratedRegex(@"^\d+/\d+$")]
    private static partial Regex UsedOfLimit();
}

/// <summary>A recipe completed from an answer: the recipe, why each guessed field holds what it does, and what else to know.</summary>
internal sealed record Inferred(HttpRecipe Recipe, IReadOnlyDictionary<string, string> Comments, IReadOnlyList<string> Notes);
