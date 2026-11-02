using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class HttpSinkTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    [Fact]
    public async Task A_page_is_one_standard_webhooks_message_and_the_same_records_always_carry_the_same_id()
    {
        var receiver = new Receiver();
        var sink = Sink(receiver, options => options.VersionedBy("$.v"));

        await sink.WriteAsync(Page("""[{ "id": 1, "v": 3, "total": 10 }, { "id": 2, "total": 20 }]"""), CancellationToken.None);
        await sink.WriteAsync(Page("""[{ "id": 1, "v": 3, "total": 10 }, { "id": 2, "total": 20 }]"""), CancellationToken.None);
        await sink.WriteAsync(Page("""[{ "id": 1, "v": 4, "total": 11 }, { "id": 2, "total": 20 }]"""), CancellationToken.None);

        var first = receiver.Received[0];
        Assert.Equal(
            """{"type":"orders","data":{"owner":"shop-7","records":[{"key":"1","version":3,"record":{"id":1,"v":3,"total":10}},{"key":"2","version":null,"record":{"id":2,"total":20}}]}}""",
            first.Body);
        Assert.Equal(first.Id, receiver.Received[1].Id);
        Assert.NotEqual(first.Id, receiver.Received[2].Id);
        Assert.Equal($"\"{first.Id}\"", first.Headers["Idempotency-Key"]);
        Assert.StartsWith("msg_", first.Id);
    }

    [Fact]
    public async Task A_signed_message_verifies_the_way_a_standard_webhooks_receiver_checks_it_and_headers_are_sent()
    {
        var receiver = new Receiver();
        var sink = Sink(receiver, options =>
        {
            options.Secret = Secret;
            options.Headers["Authorization"] = "Bearer receiver-token";
        });

        await sink.WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None);

        var message = Assert.Single(receiver.Received);
        var signed = Encoding.UTF8.GetBytes($"{message.Id}.{message.Headers["webhook-timestamp"]}.{message.Body}");
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(Secret["whsec_".Length..]), signed));
        Assert.Equal(expected, message.Headers["webhook-signature"]);
        Assert.Equal("Bearer receiver-token", message.Headers["Authorization"]);
    }

    [Fact]
    public async Task A_receiver_that_asks_to_slow_down_is_tried_again_in_the_page()
    {
        var receiver = new Receiver(count => count <= 2 ? Answer(HttpStatusCode.ServiceUnavailable, retryAfter: "0") : null);
        var sink = Sink(receiver);

        await sink.WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None);

        Assert.Equal(3, receiver.Received.Count);
        Assert.Single(receiver.Received.Select(message => message.Id).Distinct());
    }

    [Fact]
    public async Task A_receiver_that_keeps_asking_to_slow_down_postpones_the_job_and_a_long_wait_is_not_waited_in_the_page()
    {
        var busy = Sink(new Receiver(_ => Answer(HttpStatusCode.TooManyRequests)));
        var later = new Receiver(_ => Answer(HttpStatusCode.TooManyRequests, retryAfter: "120"));

        var postponed = await Assert.ThrowsAsync<JobPostponedException>(() => busy.WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None));
        var asked = await Assert.ThrowsAsync<JobPostponedException>(() => Sink(later).WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None));

        Assert.StartsWith(HttpErrorCodes.DestinationUnavailable, postponed.Message);
        Assert.Equal(TimeSpan.FromMinutes(1), postponed.Delay);
        Assert.Equal(TimeSpan.FromSeconds(120), asked.Delay);
        Assert.Single(later.Received);
    }

    [Fact]
    public async Task A_receiver_that_keeps_failing_fails_the_attempt_after_three_tries_and_never_shows_the_url()
    {
        var failing = new Receiver(_ => Answer(HttpStatusCode.InternalServerError));
        var unreachable = new Receiver(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (receiver.example.com?token=url-secret)"));

        Action<HttpSinkOptions> secrets = options =>
        {
            options.Secret = Secret;
            options.Headers["Authorization"] = "Bearer header-secret";
        };

        var failed = await Assert.ThrowsAsync<SyncLaneException>(() => Sink(failing, secrets).WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None));
        var down = await Assert.ThrowsAsync<SyncLaneException>(() => Sink(unreachable, secrets).WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None));

        Assert.Equal(3, failing.Received.Count);
        Assert.Equal(HttpErrorCodes.DestinationUnavailable, failed.Error.Code);
        Assert.Contains("500", failed.Message);
        Assert.All(new[] { failed.ToString(), down.ToString() }, text =>
        {
            Assert.DoesNotContain("url-secret", text);
            Assert.DoesNotContain("header-secret", text);
            Assert.DoesNotContain(Secret["whsec_".Length..], text);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "gone")]
    [InlineData(HttpStatusCode.Found, "redirects are not followed")]
    [InlineData(HttpStatusCode.Unauthorized, "headers")]
    [InlineData(HttpStatusCode.BadRequest, "fix the receiver")]
    public async Task A_receiver_that_will_keep_refusing_stops_the_job_at_once(HttpStatusCode status, string hint)
    {
        var receiver = new Receiver(_ => Answer(status));

        var abandoned = await Assert.ThrowsAsync<JobAbandonedException>(() => Sink(receiver).WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None));

        Assert.StartsWith(HttpErrorCodes.DestinationRejected, abandoned.Message);
        Assert.Contains(hint, abandoned.Message);
        Assert.Single(receiver.Received);
    }

    [Fact]
    public async Task A_page_too_large_for_the_receiver_is_sent_in_halves_until_it_fits()
    {
        var receiver = new Receiver((_, body) => Records(body) > 2 ? Answer(HttpStatusCode.RequestEntityTooLarge) : null);

        await Sink(receiver).WriteAsync(Page("""[{ "id": 1 }, { "id": 2 }, { "id": 3 }, { "id": 4 }, { "id": 5 }]"""), CancellationToken.None);

        var delivered = receiver.Accepted.SelectMany(message => Keys(message.Body)).ToList();
        Assert.Equal(["1", "2", "3", "4", "5"], delivered);
    }

    [Fact]
    public async Task A_single_record_the_receiver_cannot_take_stops_the_job_and_max_bytes_splits_before_sending()
    {
        var refuses = new Receiver(_ => Answer(HttpStatusCode.RequestEntityTooLarge));
        var small = new Receiver();
        var padding = new string('x', 700);

        var tooLarge = await Assert.ThrowsAsync<JobAbandonedException>(() => Sink(refuses).WriteAsync(Page("""[{ "id": 1 }]"""), CancellationToken.None));
        await Sink(small, options => options.MaxBytes = 1024).WriteAsync(Page($$"""[{ "id": 1, "p": "{{padding}}" }, { "id": 2, "p": "{{padding}}" }]"""), CancellationToken.None);

        Assert.StartsWith(HttpErrorCodes.RecordTooLarge, tooLarge.Message);
        Assert.Equal(2, small.Received.Count);
    }

    [Theory]
    [InlineData("http://receiver.example.com/sync", null, null, 1_000_000, "URL")]
    [InlineData("https://receiver.example.com/sync", "not-a-secret", null, 1_000_000, "secret")]
    [InlineData("https://receiver.example.com/sync", "whsec_c2hvcnQ=", null, 1_000_000, "secret")]
    [InlineData("https://receiver.example.com/sync", null, "webhook-id", 1_000_000, "header 'webhook-id'")]
    [InlineData("https://receiver.example.com/sync", null, null, 100, "MaxBytes")]
    public void Settings_that_cannot_work_are_refused_when_the_recipe_is_registered(string url, string? secret, string? header, int maxBytes, string setting)
    {
        var thrown = Assert.Throws<SyncLaneException>(() => new ServiceCollection().AddSyncLaneHttp(http => http
            .Recipe("orders", """{ "name": "test/orders", "request": { "url": "https://api.example.com/orders" } }""")
            .WriteToHttp(url, options =>
            {
                options.Secret = secret;
                options.MaxBytes = maxBytes;
                if (header is not null)
                    options.Headers[header] = "x";
            })));

        var error = Assert.Single(thrown.Errors);
        Assert.Equal(HttpErrorCodes.SinkSettingInvalid, error.Code);
        Assert.Contains($"destination's {setting}", error.Message);
    }

    [Fact]
    public void Http_on_this_machine_is_allowed_for_development()
    {
        var options = new HttpSinkOptions("http://localhost:5000/sync");

        Assert.Empty(options.Validate("orders"));
    }

    private static IRecordSink Sink(Receiver receiver, Action<HttpSinkOptions>? configure = null)
    {
        var options = new HttpSinkOptions("https://receiver.example.com/sync?token=url-secret");
        configure?.Invoke(options);
        Assert.Empty(options.Validate("orders"));
        options.Pauses = [TimeSpan.Zero, TimeSpan.Zero];

        var services = new ServiceCollection();
        services.AddHttpClient(HttpSource.DestinationClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new HttpSinks(factory, new HttpSourceOptions(), TimeProvider.System).For(options);
    }

    private static RecordPage Page(string records)
    {
        var job = new Job { Id = 1, Queue = "shops", Owner = "shop-7", Lane = "shop-7", Type = "orders", Mode = JobMode.Initial, Status = JobStatus.Running };
        return new RecordPage(job, "test/orders", JsonDocument.Parse(records).RootElement);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);

        return response;
    }

    private static int Records(string body) => JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("records").GetArrayLength();

    private static IEnumerable<string> Keys(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("records").EnumerateArray().Select(record => record.GetProperty("key").GetString()!).ToList();

    private sealed record Message(string Id, string Body, IReadOnlyDictionary<string, string> Headers, bool Accepted);

    /// <summary>A receiver that answers 200 unless the test says otherwise for the nth request or for a body.</summary>
    private sealed class Receiver : HttpMessageHandler
    {
        private readonly Func<int, string, HttpResponseMessage?> _answer;
        private readonly ConcurrentQueue<Message> _received = new();
        private int _count;

        public Receiver(Func<int, HttpResponseMessage?>? answer = null)
            : this((count, _) => answer?.Invoke(count))
        {
        }

        public Receiver(Func<int, string, HttpResponseMessage?> answer) => _answer = answer;

        public IReadOnlyList<Message> Received => _received.ToArray();

        public IEnumerable<Message> Accepted => Received.Where(message => message.Accepted);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            var response = _answer(Interlocked.Increment(ref _count), body);
            _received.Enqueue(new Message(headers["webhook-id"], body, headers, response is null));
            return response ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
