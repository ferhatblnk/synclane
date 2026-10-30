using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace SyncLane.Tests.Http.Support;

/// <summary>Stands in for an API: answers each request with what the test scripted and remembers what was sent.</summary>
public sealed class FakeApi(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public static FakeApi Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(Response(status, body)));

    public static HttpResponseMessage Response(HttpStatusCode status, string body = "{}") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return await answer(request, cancellationToken);
    }
}
