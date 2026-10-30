using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;

namespace SyncLane.Tests.Http.Support;

/// <summary>
/// Runs pages of one recipe against a fake API and a fake token endpoint, with one container for all pages, so a
/// token kept in memory by one page is seen by the next.
/// </summary>
public sealed class AuthHarness : IAsyncDisposable
{
    public const string TokenUrl = "https://auth.example.com/oauth/token";

    private readonly ServiceProvider _provider;

    public AuthHarness(
        string recipe,
        Func<HttpRequestMessage, HttpResponseMessage> api,
        Func<TokenCall, HttpResponseMessage>? tokens = null,
        TimeProvider? time = null,
        Action<HttpSourceOptions>? configure = null,
        Action<IServiceCollection>? register = null,
        TimeSpan? tokenDelay = null)
    {
        var fake = new FakeApi(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host != "auth.example.com")
            {
                ApiRequests.Enqueue(request);
                return api(request);
            }

            var call = await TokenCall.ReadAsync(request);
            TokenCalls.Enqueue(call);
            if (tokenDelay is { } delay)
                await Task.Delay(delay, cancellationToken);

            return (tokens ?? (_ => Token("token-1")))(call);
        });

        var services = new ServiceCollection();
        services.AddSingleton(Vault);
        services.AddSingleton(Signatures);
        if (time is not null)
            services.AddSingleton(time);

        register?.Invoke(services);
        services.AddSyncLaneHttp(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(5);
            http.Credentials<VaultStore>();
            http.Recipe("orders", recipe).WriteTo((_, _) => Task.CompletedTask);
            configure?.Invoke(http);
        });
        services.AddHttpClient(HttpSource.ClientName).ConfigurePrimaryHttpMessageHandler(() => fake);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public CredentialVault Vault { get; } = new();

    public SignatureLog Signatures { get; } = new();

    public ConcurrentQueue<HttpRequestMessage> ApiRequests { get; } = new();

    public ConcurrentQueue<TokenCall> TokenCalls { get; } = new();

    public static HttpResponseMessage Ok(string body = """{ "items": [] }""") => FakeApi.Response(HttpStatusCode.OK, body);

    public static HttpResponseMessage Token(string token, int? expiresIn = null, string? refreshToken = null) =>
        FakeApi.Response(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["access_token"] = token,
            ["token_type"] = "Bearer",
            ["expires_in"] = expiresIn,
            ["refresh_token"] = refreshToken
        }.Where(pair => pair.Value is not null).ToDictionary()));

    public async Task<JobPage> RunAsync(string owner = "shop-7", string? cursor = null, CancellationToken cancellationToken = default)
    {
        await using var scope = _provider.CreateAsyncScope();
        var job = new Job
        {
            Id = 1,
            Queue = "shops",
            Owner = owner,
            Lane = owner,
            Type = "orders",
            Mode = JobMode.Initial,
            Status = JobStatus.Running,
            Cursor = cursor
        };

        return await scope.ServiceProvider.GetRequiredService<HttpRecipeHandler>().RunPageAsync(job, cancellationToken);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>One request to the token endpoint, read while it was open.</summary>
public sealed record TokenCall(IReadOnlyDictionary<string, string> Form, string? Authorization)
{
    public static async Task<TokenCall> ReadAsync(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Decode(pair[0]), pair => Decode(pair[1]));

        return new TokenCall(form, request.Headers.Authorization?.ToString());
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}

/// <summary>Where the tests keep credentials. A singleton, so the scoped store of every page sees the same ones.</summary>
public sealed class CredentialVault
{
    public ConcurrentDictionary<CredentialKey, Credential> Credentials { get; } = new();

    public ConcurrentQueue<(CredentialKey Key, string RefreshToken)> Saved { get; } = new();

    public CredentialVault Add(string owner, string name, Credential credential)
    {
        Credentials[new CredentialKey(owner, name)] = credential;
        return this;
    }
}

/// <summary>A credential store as an application would write one: scoped, reading from storage it is given.</summary>
public sealed class VaultStore(CredentialVault vault) : ICredentialStore
{
    public ValueTask<Credential?> GetAsync(CredentialKey key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(vault.Credentials.GetValueOrDefault(key));

    public ValueTask SaveRefreshTokenAsync(CredentialKey key, string refreshToken, CancellationToken cancellationToken)
    {
        vault.Saved.Enqueue((key, refreshToken));
        vault.Credentials.AddOrUpdate(
            key,
            _ => new Credential { RefreshToken = refreshToken },
            (_, old) => new Credential { ClientId = old.ClientId, ClientSecret = old.ClientSecret, RefreshToken = refreshToken });
        return ValueTask.CompletedTask;
    }
}

public sealed class SignatureLog
{
    public ConcurrentQueue<string> Signed { get; } = new();
}

/// <summary>Signs the path and query with the credential's secret, as HMAC-style APIs ask.</summary>
public sealed class TestSigner(SignatureLog log) : IRequestSigner
{
    public ValueTask SignAsync(HttpRequestMessage request, Credential credential, CancellationToken cancellationToken)
    {
        var key = System.Text.Encoding.UTF8.GetBytes(credential.Values["secret"]);
        var data = System.Text.Encoding.UTF8.GetBytes($"{request.Method} {request.RequestUri!.PathAndQuery}");
        var signature = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(key, data));

        request.Headers.TryAddWithoutValidation("X-Signature", signature);
        log.Signed.Enqueue(signature);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A signer with a bug: it sends the request to another host.</summary>
public sealed class MovingSigner : IRequestSigner
{
    public ValueTask SignAsync(HttpRequestMessage request, Credential credential, CancellationToken cancellationToken)
    {
        request.RequestUri = new Uri("https://collector.example.net/steal");
        return ValueTask.CompletedTask;
    }
}
