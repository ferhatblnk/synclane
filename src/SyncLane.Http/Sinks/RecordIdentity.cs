using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// Tells which records of a page are the same thing: by a key read from each record, or by the record's content, and
/// optionally which copy is newer by a version. Destinations use it to write a repeated page without a duplicate, and
/// any <see cref="IRecordSink"/> can use it too.
/// </summary>
/// <remarks>
/// A page is delivered at least once: a worker that stops between the write and the cursor save writes it again, and
/// a page fetched again may hold changed records. Writing by key makes the repeat harmless; a version keeps an older
/// copy, fetched earlier but written later, from replacing a newer one.
/// </remarks>
public sealed class RecordIdentity
{
    private const int MaxCandidates = 5;

    private static readonly RecordPath Id = RecordPath.Parse("$.id")!;

    private readonly RecordPath[] _keys;
    private readonly bool _inferred;
    private readonly bool _content;
    private readonly RecordPath? _version;

    private RecordIdentity(RecordPath[] keys, bool inferred, bool content, RecordPath? version)
    {
        _keys = keys;
        _inferred = inferred;
        _content = content;
        _version = version;
    }

    /// <summary>
    /// Keys records by <c>$.id</c>, but only when every record of the page has one that is a string or a number and no
    /// two are equal. Otherwise <see cref="Identify(RecordPage)"/> fails and names the fields that could serve instead. Never a guess.
    /// </summary>
    public static RecordIdentity Inferred { get; } = new([Id], inferred: true, content: false, version: null);

    /// <summary>
    /// Keys records by a hash of their content, for records that never change, such as events or ledger lines. A record
    /// that does change becomes a new row with each version, so do not use it for those.
    /// </summary>
    public static RecordIdentity ByContent { get; } = new([], inferred: false, content: true, version: null);

    /// <summary>Keys records by the values at <paramref name="paths"/>, such as <c>$.id</c>, or several for a composite key.</summary>
    /// <param name="paths">Paths into a record, written as <c>$.name</c> or <c>$.parent.name</c>.</param>
    public static RecordIdentity ByPaths(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfZero(paths.Length);

        return new(paths.Select(path => Path(path, "key")).ToArray(), inferred: false, content: false, version: null);
    }

    /// <summary>
    /// The same identity, with a version read from <paramref name="path"/>: a number, or an ISO 8601 time. Of two copies
    /// of a record, the one with the higher version wins; a record without a version loses to one with a version.
    /// </summary>
    /// <param name="path">A path into a record, such as <c>$.updated_at</c>.</param>
    public RecordIdentity VersionedBy(string path) => new(_keys, _inferred, _content, Path(path, "version"));

    /// <summary>What the records are keyed by, for logs: <c>$.id (inferred)</c>, <c>$.a, $.b</c> or <c>content</c>.</summary>
    public string Description =>
        _content ? "content" : string.Join(", ", _keys.Select(key => key.Text)) + (_inferred ? " (inferred)" : "");

    /// <summary>The path of the version, or <c>null</c> without one.</summary>
    public string? Version => _version?.Text;

    /// <summary>Whether <paramref name="path"/> is a path this type can read: <c>$.name</c> or <c>$.parent.name</c>.</summary>
    public static bool IsPath(string? path) => path is not null && RecordPath.Parse(path) is { } parsed && parsed.Text != RecordPath.Root;

    /// <summary>
    /// The page's records with their keys and versions, one per key: of several copies in the page, the one with the
    /// highest version, or the last when versions are equal. Fails when a record has no key, or a version that cannot be
    /// compared.
    /// </summary>
    /// <param name="page">The page, as the destination received it.</param>
    public SyncResult<IReadOnlyList<IdentifiedRecord>> Identify(RecordPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Identify(page.Records, page.Recipe);
    }

    /// <summary>
    /// The same for records that did not arrive as a page, such as an answer a tool looks at: <paramref name="records"/>
    /// is a JSON array, and <paramref name="recipe"/> names it in errors.
    /// </summary>
    /// <param name="records">A JSON array of records.</param>
    /// <param name="recipe">The recipe's name, for messages.</param>
    public SyncResult<IReadOnlyList<IdentifiedRecord>> Identify(JsonElement records, string recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (records.ValueKind != JsonValueKind.Array)
            throw new ArgumentException(HttpErrors.SinkSettingInvalid(null, "records", "must be a JSON array").ToString(), nameof(records));

        var count = records.GetArrayLength();
        var survivors = new List<IdentifiedRecord>(count);
        var positions = new Dictionary<string, int>(count, StringComparer.Ordinal);

        foreach (var record in records.EnumerateArray())
        {
            var key = Key(record);
            if (key is null || (_inferred && positions.ContainsKey(key)))
                return SyncResult<IReadOnlyList<IdentifiedRecord>>.Failure(HttpErrors.RecordKeyMissing(recipe, Description, Candidates(records)));

            decimal? version = null;
            if (_version is not null && !TryVersion(_version, record, out version))
                return SyncResult<IReadOnlyList<IdentifiedRecord>>.Failure(HttpErrors.RecordVersionInvalid(recipe, _version.Text));

            var identified = new IdentifiedRecord(key, version, record);
            if (!positions.TryGetValue(key, out var position))
            {
                positions[key] = survivors.Count;
                survivors.Add(identified);
            }
            else if (!(version < survivors[position].Version) && !(version is null && survivors[position].Version is not null))
            {
                survivors[position] = identified;
            }
        }

        return SyncResult<IReadOnlyList<IdentifiedRecord>>.Success(survivors);
    }

    private string? Key(JsonElement record)
    {
        if (_content)
            return ContentHash(record);

        if (_keys.Length == 1)
            return _keys[0].TryFind(record, out var value) ? Scalar(value) : null;

        // Sequential: the parts of a composite key, in the order they were declared.
        var parts = new string[_keys.Length];
        for (var index = 0; index < _keys.Length; index++)
        {
            if (!_keys[index].TryFind(record, out var value) || Scalar(value) is not { } part)
                return null;

            parts[index] = part;
        }

        // A JSON array cannot be confused with another combination of parts, as a separator could.
        return JsonSerializer.Serialize(parts, RecordIdentityJson.Default.StringArray);
    }

    /// <summary>A string as it is, a whole number in plain digits, any other number as the API wrote it; nothing else.</summary>
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() is { Length: > 0 } text => text,
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : value.GetRawText(),
        _ => null
    };

    /// <summary>
    /// A number as it is; an ISO 8601 time as microseconds since 1970, so times in different offsets compare correctly
    /// and never as text; a missing or null value as no version.
    /// </summary>
    private static bool TryVersion(RecordPath path, JsonElement record, out decimal? version)
    {
        version = null;
        if (!path.TryFind(record, out var value) || value.ValueKind == JsonValueKind.Null)
            return true;

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out var number))
                return false;

            version = number;
            return true;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || !LooksLikeIsoDate(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
        {
            return false;
        }

        version = (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (decimal)TimeSpan.TicksPerMicrosecond;
        return true;
    }

    private static bool LooksLikeIsoDate(string text) =>
        text.Length >= 10 && char.IsAsciiDigit(text[0]) && char.IsAsciiDigit(text[1]) && char.IsAsciiDigit(text[2])
        && char.IsAsciiDigit(text[3]) && text[4] == '-' && char.IsAsciiDigit(text[5]) && char.IsAsciiDigit(text[6])
        && text[7] == '-' && char.IsAsciiDigit(text[8]) && char.IsAsciiDigit(text[9]);

    /// <summary>
    /// A hash of the record written in one canonical form: properties sorted, no white space. The same record sent in
    /// another property order or layout has the same hash.
    /// </summary>
    private static string ContentHash(JsonElement record)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, record);

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    /// <summary>Top-level fields that could key the page: in every record, a string or a number, and unique.</summary>
    private static string[] Candidates(JsonElement records)
    {
        var all = records.EnumerateArray().ToList();
        if (all.Count == 0 || all[0].ValueKind != JsonValueKind.Object)
            return [];

        return all[0].EnumerateObject()
            .Select(property => property.Name)
            .Where(name => all.Select(record => record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var value) ? Scalar(value) : null)
                .ToList() is var values && values.All(value => value is not null) && values.Distinct(StringComparer.Ordinal).Count() == values.Count)
            .OrderBy(name => name.EndsWith("id", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .Select(name => $"$.{name}")
            .ToArray();
    }

    private static RecordPath Path(string path, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return IsPath(path)
            ? RecordPath.Parse(path)!
            : throw new ArgumentException(HttpErrors.SinkSettingInvalid(null, what, PathRule(path)).ToString(), what == "key" ? "paths" : nameof(path));
    }

    /// <summary>Why <paramref name="path"/> cannot be read, worded for an error message.</summary>
    internal static string PathRule(string path) => $"'{path}' must be a path such as $.id or $.parent.id";
}

/// <summary>A record of a page with the key and version its destination writes it by.</summary>
/// <param name="Key">The key: the value at the key path, a JSON array of the values of a composite key, or a content hash.</param>
/// <param name="Version">The version, or <c>null</c> without one. ISO 8601 times are in microseconds since 1970.</param>
/// <param name="Record">The record. Valid only during the call that received the page, like <see cref="RecordPage.Records"/>.</param>
public readonly record struct IdentifiedRecord(string Key, decimal? Version, JsonElement Record);

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]), TypeInfoPropertyName = "StringArray")]
internal sealed partial class RecordIdentityJson : System.Text.Json.Serialization.JsonSerializerContext;
