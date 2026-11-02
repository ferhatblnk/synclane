using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncLane.Http;

/// <summary>The HTTP destinations of the registered recipes: one writer per recipe.</summary>
internal sealed class HttpSinks(IHttpClientFactory clients, HttpSourceOptions source, TimeProvider time)
{
    private readonly ConcurrentDictionary<HttpSinkOptions, HttpWriter> _writers = new();

    public IRecordSink For(HttpSinkOptions options) => _writers.GetOrAdd(options, created => new HttpWriter(created, clients, source, time));
}

/// <summary>
/// Posts a recipe's pages to a receiver, as Standard Webhooks messages whose data is the page's records with their keys
/// and versions. The body holds no time, so the same records always make the same bytes and the same id: a page sent
/// again carries the id it had, and a page whose records changed carries a new one.
/// </summary>
internal sealed class HttpWriter(HttpSinkOptions options, IHttpClientFactory clients, HttpSourceOptions source, TimeProvider time) : IRecordSink
{
    private static readonly TimeSpan LongestWaitInPage = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PostponeWithoutAWait = TimeSpan.FromMinutes(1);

    public async Task WriteAsync(RecordPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var identified = options.Identity.Identify(page);
        if (!identified.Succeeded)
            throw new JobAbandonedException(string.Join("; ", identified.Errors));

        if (identified.Value!.Count > 0)
            await PostAsync(page.Job, identified.Value, 0, identified.Value.Count, cancellationToken);
    }

    /// <summary>Posts records <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/>: whole, or in halves when they are too large.</summary>
    private async Task PostAsync(Job job, IReadOnlyList<IdentifiedRecord> records, int start, int count, CancellationToken cancellationToken)
    {
        var body = Envelope(job, records, start, count);
        if (body.Length <= options.MaxBytes && await SendAsync(job, body, cancellationToken))
            return;

        if (count == 1)
            throw new JobAbandonedException(HttpErrors.RecordTooLarge(job.Type, options.MaxBytes).ToString());

        await PostAsync(job, records, start, count / 2, cancellationToken);
        await PostAsync(job, records, start + count / 2, count - count / 2, cancellationToken);
    }

    /// <summary>
    /// Sends one body, trying three times. Returns whether it was taken; <c>false</c> means the receiver found it too
    /// large (413). Throws for everything else that did not end in a 2xx.
    /// </summary>
    private async Task<bool> SendAsync(Job job, byte[] body, CancellationToken cancellationToken)
    {
        var id = "msg_" + Convert.ToHexStringLower(SHA256.HashData(body));
        var client = clients.CreateClient(HttpSource.DestinationClientName);

        // Sequential: a retry follows the try before it.
        for (var tried = 0; ; tried++)
        {
            string failure;
            TimeSpan? asked = null;
            var slowDown = false;

            using var timeout = new CancellationTokenSource(source.Timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var request = Request(id, body);
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                var status = (int)response.StatusCode;
                if (status is >= 200 and < 300)
                    return true;

                if (status == 413)
                    return false;

                slowDown = status is 429 or 502 or 503 or 504;
                if (!slowDown && status is >= 300 and < 500 && status != 408)
                    throw new JobAbandonedException(HttpErrors.DestinationRejected(job.Type, status).ToString());

                failure = status.ToString(CultureInfo.InvariantCulture);
                asked = RetryAfter.From(response, time.GetUtcNow(), source.MaxRetryAfter);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                failure = "no answer in time";
            }
            catch (HttpRequestException exception)
            {
                // The exception's own message may hold the URL, which may hold a token: only its kind is kept.
                failure = HttpErrors.NetworkFailure(exception.HttpRequestError);
            }

            // A receiver that asks for a long wait gets it without holding the worker, and without using an attempt.
            if (slowDown && (tried >= options.Pauses.Length || asked > LongestWaitInPage))
                throw new JobPostponedException(HttpErrors.DestinationUnavailable(job.Type, failure).ToString(), asked ?? PostponeWithoutAWait);

            if (tried >= options.Pauses.Length)
                throw new SyncLaneException(HttpErrors.DestinationUnavailable(job.Type, failure));

            await Task.Delay(asked ?? Jitter(options.Pauses[tried]), time, cancellationToken);
        }
    }

    private HttpRequestMessage Request(string id, byte[] body)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, options.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("User-Agent", source.UserAgent);
        request.Headers.TryAddWithoutValidation("webhook-id", id);
        request.Headers.TryAddWithoutValidation("webhook-timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", $"\"{id}\"");

        if (options.SecretKey is { } key)
            request.Headers.TryAddWithoutValidation("webhook-signature", "v1," + Sign(key, id, timestamp, body));

        foreach (var (name, value) in options.Headers)
            request.Headers.TryAddWithoutValidation(name, value);

        return request;
    }

    /// <summary>The Standard Webhooks signature: HMAC-SHA256 of <c>id.timestamp.body</c>, in base64.</summary>
    internal static string Sign(byte[] key, string id, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed, prefix.Length);
        return Convert.ToBase64String(HMACSHA256.HashData(key, signed));
    }

    /// <summary><c>{ "type": job type, "data": { "owner": ..., "records": [ { "key", "version", "record" } ] } }</c>.</summary>
    private static byte[] Envelope(Job job, IReadOnlyList<IdentifiedRecord> records, int start, int count)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", job.Type);
            writer.WriteStartObject("data");
            writer.WriteString("owner", job.Owner);
            writer.WriteStartArray("records");
            for (var index = start; index < start + count; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("key", records[index].Key);
                if (records[index].Version is { } version)
                    writer.WriteNumber("version", version);
                else
                    writer.WriteNull("version");

                writer.WritePropertyName("record");
                records[index].Record.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A pause of 80% to 120% of <paramref name="pause"/>, so the retries of many jobs do not arrive together.</summary>
    private static TimeSpan Jitter(TimeSpan pause) => pause * (0.8 + Random.Shared.NextDouble() * 0.4);
}
