namespace SyncLane.AspNetCore;

/// <summary>
/// An enum taken from the query string by name, in any case: <c>pending</c> and <c>Pending</c> are the same status. The
/// framework binds enums case-sensitively, while the endpoints answer in camel case, so a client that sends back what it
/// read would otherwise be refused.
/// </summary>
/// <typeparam name="T">The enum.</typeparam>
internal readonly record struct QueryEnum<T>(T Value) where T : struct, Enum
{
    /// <summary>Found by the framework when it binds a query value.</summary>
    public static bool TryParse(string? text, out QueryEnum<T> result)
    {
        if (Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value))
        {
            result = new QueryEnum<T>(value);
            return true;
        }

        result = default;
        return false;
    }
}
