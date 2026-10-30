namespace SyncLane.Http;

/// <summary>
/// The secret values an owner's requests are sent with, as an <see cref="ICredentialStore"/> returns them. Set the
/// values the recipe's auth type needs; the others are ignored. <see cref="ToString"/> never shows a value, so a
/// credential written to a log by mistake gives nothing away.
/// </summary>
/// <remarks>
/// .NET cannot wipe a string from memory, so SyncLane does not pretend to: a credential lives as long as the page that
/// uses it, and an OAuth 2.0 access token as long as it is valid. Neither is ever written anywhere.
/// </remarks>
public sealed class Credential
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    private readonly IReadOnlyDictionary<string, string> _values = None;

    /// <summary>The token for <c>bearer</c>, or the key for <c>apiKey</c>.</summary>
    public string? Token { get; init; }

    /// <summary>The user name for <c>basic</c>. It must not contain a colon.</summary>
    public string? Username { get; init; }

    /// <summary>The password for <c>basic</c>. May be left out, for APIs that take a key as the user name.</summary>
    public string? Password { get; init; }

    /// <summary>The client id for <c>oauth2</c>.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// The client secret for <c>oauth2</c>. Required for the client credentials grant; leave it out for a public
    /// client that uses a refresh token.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>The refresh token for <c>oauth2</c> with the refresh token grant.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>Any other values, for an <see cref="IRequestSigner"/>.</summary>
    public IReadOnlyDictionary<string, string> Values { get => _values; init => _values = value ?? None; }

    /// <summary>Names the values that are set, never the values themselves.</summary>
    public override string ToString()
    {
        var set = new List<string>(7);
        Add(set, nameof(Token), Token);
        Add(set, nameof(Username), Username);
        Add(set, nameof(Password), Password);
        Add(set, nameof(ClientId), ClientId);
        Add(set, nameof(ClientSecret), ClientSecret);
        Add(set, nameof(RefreshToken), RefreshToken);
        if (_values.Count > 0)
            set.Add(nameof(Values));

        return set.Count == 0 ? "Credential (empty)" : $"Credential ({string.Join(", ", set)} set; values hidden)";
    }

    private static void Add(List<string> set, string name, string? value)
    {
        if (value is not null)
            set.Add(name);
    }
}
