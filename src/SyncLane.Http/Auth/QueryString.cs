namespace SyncLane.Http;

/// <summary>Adds and removes one query parameter, leaving the rest of the URL exactly as written.</summary>
internal static class QueryString
{
    /// <summary><paramref name="uri"/> with <paramref name="name"/> set to <paramref name="value"/>, replacing any value it had.</summary>
    public static Uri With(Uri uri, string name, string value)
    {
        var without = Without(uri, name);
        var query = without.Query.TrimStart('?');
        var pair = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

        return new Uri($"{without.GetLeftPart(UriPartial.Path)}?{(query.Length == 0 ? pair : $"{query}&{pair}")}", UriKind.Absolute);
    }

    /// <summary><paramref name="uri"/> without any <paramref name="name"/> parameter.</summary>
    public static Uri Without(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
            return uri;

        var pairs = query.Split('&');
        var kept = pairs.Where(pair => pair.Length > 0 && !IsNamed(pair, name)).ToArray();
        if (kept.Length == pairs.Length)
            return uri;

        var path = uri.GetLeftPart(UriPartial.Path);
        return new Uri(kept.Length == 0 ? path : $"{path}?{string.Join('&', kept)}", UriKind.Absolute);
    }

    private static bool IsNamed(string pair, string name)
    {
        var end = pair.IndexOf('=', StringComparison.Ordinal);
        var raw = end < 0 ? pair : pair[..end];
        return string.Equals(Uri.UnescapeDataString(raw.Replace('+', ' ')), name, StringComparison.Ordinal);
    }
}
