namespace SyncLane.Http;

/// <summary>
/// Where and how a recipe's records are posted, set in <see cref="HttpRecipeBinding.WriteToHttp"/>. Only the URL is
/// needed: records are keyed by <c>$.id</c> when every record has a unique one, and requests are unsigned.
/// </summary>
public sealed class HttpSinkOptions
{
    private const string SecretPrefix = "whsec_";
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Host", "Idempotency-Key", "webhook-id", "webhook-timestamp", "webhook-signature"
    };

    private readonly RecordKeySettings _keys = new();

    internal HttpSinkOptions(string url) => Url = url;

    /// <summary>The receiver: https, or http on this machine for development. Never logged; it may hold a token.</summary>
    public string Url { get; }

    /// <summary>
    /// A Standard Webhooks secret, <c>whsec_</c> followed by 24 to 64 random bytes in base64. Every request is then
    /// signed, and the receiver can check it with any Standard Webhooks library. Leave it out to send unsigned.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>Headers sent with every request, such as <c>Authorization</c>. Never logged.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The largest request body. A page that would be larger is sent in parts. Default one megabyte.</summary>
    public int MaxBytes { get; set; } = 1_000_000;

    /// <summary>Keys records by the values at <paramref name="paths"/>, such as <c>$.id</c>, or several for a composite key.</summary>
    public HttpSinkOptions KeyedBy(params string[] paths)
    {
        _keys.KeyedBy(paths);
        return this;
    }

    /// <summary>Keys records by a hash of their content: for records that never change, such as events or ledger lines.</summary>
    public HttpSinkOptions KeyedByContent()
    {
        _keys.KeyedByContent();
        return this;
    }

    /// <summary>Sends a version read at <paramref name="path"/>, a number or an ISO 8601 time, so the receiver can keep the newer copy.</summary>
    public HttpSinkOptions VersionedBy(string path)
    {
        _keys.VersionedBy(path);
        return this;
    }

    internal RecordIdentity Identity => _keys.Identity;

    /// <summary>The secret's bytes, or <c>null</c> without one. Valid after <see cref="Validate"/>.</summary>
    internal byte[]? SecretKey { get; private set; }

    /// <summary>Waits between tries of one request. Tests shorten them.</summary>
    internal TimeSpan[] Pauses { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    internal List<SyncError> Validate(string type)
    {
        var errors = new List<SyncError>();
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            errors.Add(HttpErrors.SinkSettingInvalid(type, "URL", "must be an absolute https URL, or http on this machine (localhost) for development"));
        }

        if (Secret is not null)
        {
            SecretKey = Decode(Secret);
            if (SecretKey is null)
                errors.Add(HttpErrors.SinkSettingInvalid(type, "secret", "must be 'whsec_' followed by 24 to 64 bytes in base64, as Standard Webhooks writes it"));
        }

        errors.AddRange(Headers
            .Where(header => Reserved.Contains(header.Key) || header.Key.Length == 0 || header.Key.Any(c => c <= ' ' || c == ':')
                || header.Value.AsSpan().ContainsAny('\r', '\n'))
            .Select(header => HttpErrors.SinkSettingInvalid(type, $"header '{header.Key}'", "is set by SyncLane, or is not a valid header; its value must be one line")));

        if (MaxBytes < 1024)
            errors.Add(HttpErrors.SinkSettingInvalid(type, "MaxBytes", "must be at least 1024"));

        _keys.Validate(type, errors);
        return errors;
    }

    private static byte[]? Decode(string secret)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
            return null;

        var bytes = new byte[secret.Length];
        return Convert.TryFromBase64String(secret[SecretPrefix.Length..], bytes, out var written) && written is >= 24 and <= 64
            ? bytes[..written]
            : null;
    }
}
