using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SyncLane.Cli.Curl;
using SyncLane.Http;

namespace SyncLane.Cli.Probe;

/// <summary>
/// Sends a draft's request once, with the credential the curl carried, and completes the recipe from the answer. The
/// credential is used for this request only; it is not logged, kept or written.
/// </summary>
internal static class Prober
{
    /// <summary>The User-Agent recipes send, so the API answers the probe as it will answer the jobs.</summary>
    private static readonly string UserAgent = new HttpSourceOptions().UserAgent;

    public static async Task<SyncResult<Inferred>> ProbeAsync(RecipeDraft draft, HttpClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(client);

        using var request = Request(draft);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // Only the kind: the message of a network exception can hold the URL, and the URL can hold a key.
            return SyncResult<Inferred>.Failure(CliErrors.ProbeUnreachable(exception.HttpRequestError));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SyncResult<Inferred>.Failure(CliErrors.ProbeTimedOut(client.Timeout));
        }

        using var answer = response;
        if (!response.IsSuccessStatusCode)
            return SyncResult<Inferred>.Failure(CliErrors.ProbeRefused((int)response.StatusCode));

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return SyncResult<Inferred>.Failure(CliErrors.ProbeNotJson(response.Content.Headers.ContentType?.MediaType ?? "no content type"));
        }

        using (document)
        {
            var headers = response.Headers.Concat(response.Content.Headers)
                .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => string.Join(", ", group.First().Value), StringComparer.OrdinalIgnoreCase);

            var inferred = Inference.Infer(draft.Recipe, document.RootElement, headers);
            return SyncResult<Inferred>.Success(inferred with { Notes = [.. draft.Notes, .. inferred.Notes] });
        }
    }

    /// <summary>The draft's request as the curl sent it: its original query values, and its credential in place.</summary>
    internal static HttpRequestMessage Request(RecipeDraft draft)
    {
        var recipe = draft.Recipe.Request;
        var query = recipe.Query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}").ToList();
        var secret = draft.Secret;
        if (secret is { Auth: { Type: AuthType.ApiKey, In: ApiKeyLocation.Query } auth })
            query.Add($"{Uri.EscapeDataString(auth.Name!)}={Uri.EscapeDataString(secret.Value)}");

        var url = query.Count == 0 ? recipe.Url : $"{recipe.Url}?{string.Join('&', query)}";
        var request = new HttpRequestMessage(new HttpMethod(recipe.Method), url);
        foreach (var (name, value) in recipe.Headers)
            request.Headers.TryAddWithoutValidation(name, value);

        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (recipe.Body is { } body)
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        switch (secret?.Auth)
        {
            case { Type: AuthType.Bearer }:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Value);
                break;
            case { Type: AuthType.Basic }:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret.Value)));
                break;
            case { Type: AuthType.ApiKey, In: not ApiKeyLocation.Query } header:
                request.Headers.TryAddWithoutValidation(header.Name!, (header.Prefix ?? "") + secret.Value);
                break;
        }

        return request;
    }
}
