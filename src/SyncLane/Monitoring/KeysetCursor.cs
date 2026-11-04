using System.Buffers.Text;
using System.Text.Json;

namespace SyncLane.Monitoring;

/// <summary>
/// Turns the sort key of the last row of a page into an opaque cursor and back. Owner keys can hold any character, so
/// the parts are a JSON array of strings, in base64url so the cursor is safe in a query string.
/// </summary>
internal static class KeysetCursor
{
    public static string Encode(params string[] parts)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var part in parts)
                json.WriteStringValue(part);
            json.WriteEndArray();
        }

        return Base64Url.EncodeToString(buffer.ToArray());
    }

    public static bool TryDecode(string cursor, int count, out string[] parts)
    {
        parts = [];
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor));
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != count)
                return false;

            var values = new string[count];
            var index = 0;
            foreach (var part in document.RootElement.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.String)
                    return false;
                values[index++] = part.GetString()!;
            }

            parts = values;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
