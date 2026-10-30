using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// Fetches OAuth 2.0 access tokens and keeps them in memory until shortly before they expire, one per owner and
/// credential. Pages that need a token at the same moment wait for one request instead of each sending their own.
/// Tokens are never written anywhere, so a restarted process fetches new ones.
/// </summary>
internal sealed class OAuthTokens(IHttpClientFactory clients, HttpSourceOptions options, TimeProvider time)
{
    private const int MaxAnswerBytes = 64 * 1024;

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxMargin = TimeSpan.FromMinutes(1);

    /// <summary>The error codes of RFC 6749, section 5.2. Only these are quoted in a message; nothing else an endpoint says is.</summary>
    private static readonly FrozenSet<string> StandardErrors = new[]
    {
        "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client", "unsupported_grant_type", "invalid_scope"
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<SlotKey, Slot> _slots = new();

    /// <summary>A valid token for the page's owner, fetched only when none is kept or <paramref name="rejected"/> is the one kept.</summary>
    public async ValueTask<AuthStamp> GetAsync(OAuth2Auth auth, AuthContext context, string? rejected, CancellationToken cancellationToken)
    {
        var slot = _slots.GetOrAdd(new SlotKey(auth.TokenUrl.AbsoluteUri, auth.Scope, auth.Grant, context.Key), static _ => new Slot());
        var fingerprint = Fingerprint(auth, context.Credential, context.Credential.RefreshToken);

        if (slot.Use(fingerprint, rejected, time.GetUtcNow()) is { } kept)
            return new AuthStamp(kept, fresh: false);

        await slot.Gate.WaitAsync(cancellationToken);
        try
        {
            // Another page may have fetched one while this one waited.
            if (slot.Use(fingerprint, rejected, time.GetUtcNow()) is { } fetched)
                return new AuthStamp(fetched, fresh: false);

            var issued = (await RequestAsync(auth, context, context.Credential, mayReread: true, cancellationToken)).AlsoFor(fingerprint);
            slot.Keep(issued);
            return new AuthStamp(issued.Token, fresh: true);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private async Task<Issued> RequestAsync(OAuth2Auth auth, AuthContext context, Credential credential, bool mayReread, CancellationToken cancellationToken)
    {
        var (answer, refusal) = await SendAsync(auth, credential, cancellationToken);

        if (answer is null)
        {
            // A rotating refresh token may have just been used and replaced by another instance; the store then holds
            // its successor, and one more try with it settles the race.
            if (mayReread && auth.Grant == OAuth2Grant.RefreshToken && refusal.Error == "invalid_grant"
                && await context.Store.GetAsync(context.Key, cancellationToken) is { RefreshToken: { Length: > 0 } newer } stored
                && newer != credential.RefreshToken)
                return await RequestAsync(auth, context, stored, mayReread: false, cancellationToken);

            throw new JobAbandonedException(HttpErrors.TokenRefused(auth.Recipe, refusal.Status, refusal.Error).ToString());
        }

        if (answer.RotatedRefreshToken is { } rotated)
            await context.Store.SaveRefreshTokenAsync(context.Key, rotated, CancellationToken.None);

        var renewAt = answer.Lifetime is { } lifetime ? time.GetUtcNow() + lifetime - Margin(lifetime) : (DateTimeOffset?)null;
        string[] fingerprints = answer.RotatedRefreshToken is { } next
            ? [Fingerprint(auth, credential, credential.RefreshToken), Fingerprint(auth, credential, next)]
            : [Fingerprint(auth, credential, credential.RefreshToken)];

        return new Issued(answer.Token, renewAt, fingerprints);
    }

    /// <summary>Asks the token endpoint once: a token, or the status and standard error code it refused with.</summary>
    private async Task<(TokenAnswer? Answer, Refusal Refusal)> SendAsync(OAuth2Auth auth, Credential credential, CancellationToken cancellationToken)
    {
        var clientId = auth.Required(credential.ClientId, nameof(Credential.ClientId));
        var secret = auth.Grant == OAuth2Grant.ClientCredentials
            ? auth.Required(credential.ClientSecret, nameof(Credential.ClientSecret))
            : string.IsNullOrEmpty(credential.ClientSecret) ? null : credential.ClientSecret;
        var refreshToken = auth.Grant == OAuth2Grant.RefreshToken ? auth.Required(credential.RefreshToken, nameof(Credential.RefreshToken)) : null;

        using var request = Build(auth, clientId, secret, refreshToken);
        using var timeout = new CancellationTokenSource(options.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var response = await clients.CreateClient(HttpSource.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var status = (int)response.StatusCode;

            if (status is >= 200 and < 300)
                return (await ReadAnswerAsync(auth, credential, response.Content, linked.Token), default);

            if (status is 400 or 401)
                return (null, new Refusal(status, await ReadErrorAsync(response.Content, linked.Token)));

            throw status switch
            {
                429 => new JobThrottledException(
                    HttpErrors.TokenUnavailable(auth.Recipe, "answered 429 and asked to slow down").ToString(),
                    RetryAfter.From(response, time.GetUtcNow(), options.MaxRetryAfter)),
                408 or >= 500 => new SyncLaneException(HttpErrors.TokenUnavailable(auth.Recipe, $"answered {status}")),
                _ => (Exception)new JobAbandonedException(HttpErrors.TokenRefused(auth.Recipe, status, null).ToString())
            };
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SyncLaneException(
                HttpErrors.TokenUnavailable(auth.Recipe, $"did not answer within {options.Timeout.TotalSeconds:0.#} s"), exception);
        }
        catch (HttpRequestException exception)
        {
            // The exception's own message may hold the URL or a proxy's credentials, so only its kind is kept.
            throw new SyncLaneException(HttpErrors.TokenUnavailable(auth.Recipe, HttpErrors.NetworkFailure(exception.HttpRequestError)));
        }
    }

    private HttpRequestMessage Build(OAuth2Auth auth, string clientId, string? secret, string? refreshToken)
    {
        var form = new List<KeyValuePair<string, string>>(5)
        {
            new("grant_type", auth.Grant == OAuth2Grant.ClientCredentials ? "client_credentials" : "refresh_token")
        };

        if (refreshToken is not null)
            form.Add(new("refresh_token", refreshToken));

        if (auth.Scope is not null)
            form.Add(new("scope", auth.Scope));

        var request = new HttpRequestMessage(HttpMethod.Post, auth.TokenUrl);
        if (secret is not null && auth.ClientAuth == OAuth2ClientAuth.Basic)
        {
            // RFC 6749, section 2.3.1: the id and secret are form-encoded before they are joined and Base64-encoded.
            var pair = Encoding.UTF8.GetBytes($"{FormEncode(clientId)}:{FormEncode(secret)}");
            request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(pair));
        }
        else
        {
            form.Add(new("client_id", clientId));
            if (secret is not null)
                form.Add(new("client_secret", secret));
        }

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        return request;
    }

    private static async Task<TokenAnswer> ReadAnswerAsync(OAuth2Auth auth, Credential credential, HttpContent content, CancellationToken cancellationToken)
    {
        var (document, tooLarge) = await JsonBody.ReadAsync(content, MaxAnswerBytes, cancellationToken);
        if (document is null)
            throw Invalid(auth, tooLarge ? "is larger than 64 KiB" : "is not JSON");

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var accessToken)
                || accessToken.ValueKind != JsonValueKind.String || accessToken.GetString() is not { Length: > 0 } token)
                throw Invalid(auth, "has no access_token");

            if (root.TryGetProperty("token_type", out var type)
                && !(type.ValueKind == JsonValueKind.String && string.Equals(type.GetString(), "bearer", StringComparison.OrdinalIgnoreCase)))
                throw Invalid(auth, "has a token_type other than bearer, the only type SyncLane sends");

            if (token.AsSpan().ContainsAnyExceptInRange('!', '~'))
                throw Invalid(auth, "has an access_token with characters a header cannot carry");

            var rotated = auth.Grant == OAuth2Grant.RefreshToken
                && root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String
                && refresh.GetString() is { Length: > 0 } next && next != credential.RefreshToken
                    ? next
                    : null;

            return new TokenAnswer(token, Lifetime(root), rotated);
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var (document, _) = await JsonBody.ReadAsync(content, MaxAnswerBytes, cancellationToken);
        using (document)
        {
            return document?.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } code && StandardErrors.Contains(code)
                    ? code
                    : null;
        }
    }

    /// <summary>How long the token lives, at most a day. Without a readable <c>expires_in</c> it is kept until the API answers 401.</summary>
    private static TimeSpan? Lifetime(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out var value))
            return null;

        var seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) => text,
            _ => double.NaN
        };

        return seconds > 0 ? TimeSpan.FromSeconds(Math.Min(seconds, MaxLifetime.TotalSeconds)) : null;
    }

    /// <summary>A token is renewed a little before it expires, so a request never leaves with one that dies on the way.</summary>
    private static TimeSpan Margin(TimeSpan lifetime) => lifetime / 10 < MaxMargin ? lifetime / 10 : MaxMargin;

    private static JobAbandonedException Invalid(OAuth2Auth auth, string problem) =>
        new(HttpErrors.TokenAnswerInvalid(auth.Recipe, problem).ToString());

    private static string FormEncode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>Which credential a token was issued for, without keeping the secrets themselves.</summary>
    private static string Fingerprint(OAuth2Auth auth, Credential credential, string? refreshToken)
    {
        var lineage = auth.Grant == OAuth2Grant.RefreshToken ? refreshToken : null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{credential.ClientId}\0{credential.ClientSecret}\0{lineage}")));
    }

    private readonly record struct SlotKey(string TokenUrl, string? Scope, OAuth2Grant Grant, CredentialKey Key);

    private readonly record struct Refusal(int Status, string? Error);

    /// <summary>A token endpoint's answer. A class, not a record, so nothing prints the token.</summary>
    private sealed class TokenAnswer(string token, TimeSpan? lifetime, string? rotatedRefreshToken)
    {
        public string Token => token;

        public TimeSpan? Lifetime => lifetime;

        public string? RotatedRefreshToken => rotatedRefreshToken;
    }

    /// <summary>A kept token, and the credentials it may be used for: the one it was issued for and, after a rotation, its successor.</summary>
    private sealed class Issued(string token, DateTimeOffset? renewAt, string[] fingerprints)
    {
        public string Token => token;

        public DateTimeOffset? RenewAt => renewAt;

        public bool IsFor(string fingerprint) => Array.IndexOf(fingerprints, fingerprint) >= 0;

        public Issued AlsoFor(string fingerprint) => IsFor(fingerprint) ? this : new(token, renewAt, [.. fingerprints, fingerprint]);
    }

    private sealed class Slot
    {
        private volatile Issued? _current;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public string? Use(string fingerprint, string? rejected, DateTimeOffset now) =>
            _current is { } current && (current.RenewAt is null || now < current.RenewAt) && current.IsFor(fingerprint) && current.Token != rejected
                ? current.Token
                : null;

        public void Keep(Issued issued) => _current = issued;
    }
}
