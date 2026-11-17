using System.Text.Json;

namespace SyncLane.AspNetCore;

/// <summary>
/// An enum taken from the query string by the same word the endpoints answer with (<c>pending</c>): it is read with the
/// JSON options that write the answers, so what a client reads is what it can send back. The framework's own binding
/// would take the member's name, <c>Pending</c>, and nothing else.
/// </summary>
/// <typeparam name="T">The enum.</typeparam>
internal readonly record struct QueryEnum<T>(T Value) where T : struct, Enum
{
    /// <summary>Found by the framework when it binds a query value.</summary>
    public static bool TryParse(string? text, out QueryEnum<T> result)
    {
        result = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(text), SyncLaneEndpoints.Json);
            if (!Enum.IsDefined(value))
            {
                return false;
            }

            result = new QueryEnum<T>(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
