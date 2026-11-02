using SyncLane.Http;

namespace SyncLane.Cli.Commands;

/// <summary>
/// The one credential a recipe test needs: each value from <c>SYNCLANE_&lt;CREDENTIAL&gt;_&lt;FIELD&gt;</c>, or typed in
/// the terminal without being shown. Values stay in memory for the test and are never printed.
/// </summary>
internal sealed class TestCredentials(TestCredentials.Given given) : ICredentialStore
{
    private sealed record Field(string Name, string Label, bool Required);

    /// <summary>The values gathered for the test, shared by every scope that asks for them.</summary>
    internal sealed class Given(Credential credential)
    {
        public Credential Credential => credential;

        /// <summary>Whether the token endpoint replaced the refresh token during the test.</summary>
        public bool Rotated { get; set; }
    }

    public ValueTask<Credential?> GetAsync(CredentialKey key, CancellationToken cancellationToken) => ValueTask.FromResult<Credential?>(given.Credential);

    public ValueTask SaveRefreshTokenAsync(CredentialKey key, string refreshToken, CancellationToken cancellationToken)
    {
        given.Rotated = true;
        return ValueTask.CompletedTask;
    }

    public static SyncResult<Given> Gather(HttpRecipeAuth auth, CliHost host)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(host);

        var prefix = $"SYNCLANE_{new string([.. auth.Credential.ToUpperInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')])}_";
        var values = new Dictionary<string, string>();
        var missing = new List<string>();

        foreach (var field in Fields(auth))
        {
            var variable = prefix + field.Name;
            var value = host.Environment(variable) is { Length: > 0 } set ? set : host.AskSecret?.Invoke($"{auth.Credential} {field.Label}{(field.Required ? "" : " (Enter for none)")}: ");
            if (value is { Length: > 0 })
                values[field.Name] = value;
            else if (field.Required)
                missing.Add(variable);
        }

        if (missing.Count > 0)
            return SyncResult<Given>.Failure(CliErrors.CredentialNotGiven(auth.Credential, missing));

        return SyncResult<Given>.Success(new Given(new Credential
        {
            Token = values.GetValueOrDefault("TOKEN"),
            Username = values.GetValueOrDefault("USERNAME"),
            Password = values.GetValueOrDefault("PASSWORD"),
            ClientId = values.GetValueOrDefault("CLIENT_ID"),
            ClientSecret = values.GetValueOrDefault("CLIENT_SECRET"),
            RefreshToken = values.GetValueOrDefault("REFRESH_TOKEN")
        }));
    }

    private static IEnumerable<Field> Fields(HttpRecipeAuth auth) => auth.Type switch
    {
        AuthType.Basic => [new("USERNAME", "user name", true), new("PASSWORD", "password", false)],
        AuthType.OAuth2 when auth.Grant == OAuth2Grant.RefreshToken =>
            [new("CLIENT_ID", "client id", true), new("CLIENT_SECRET", "client secret", false), new("REFRESH_TOKEN", "refresh token", true)],
        AuthType.OAuth2 => [new("CLIENT_ID", "client id", true), new("CLIENT_SECRET", "client secret", true)],
        _ => [new("TOKEN", auth.Type == AuthType.ApiKey ? "key" : "token", true)]
    };
}
