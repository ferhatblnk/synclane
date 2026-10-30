using System.Buffers;
using System.Text.Json;

namespace SyncLane.Http;

/// <summary>Reads a JSON body up to a size limit. The body never reaches a message: the parser's own message quotes it.</summary>
internal static class JsonBody
{
    private const int ReadChunk = 81920;

    /// <summary>The parsed body; or no document, with <c>TooLarge</c> saying whether it was too large or not JSON.</summary>
    public static async Task<(JsonDocument? Document, bool TooLarge)> ReadAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit)
            return (null, true);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new ArrayBufferWriter<byte>();

        // Sequential: reading one stream.
        while (true)
        {
            var read = await stream.ReadAsync(buffer.GetMemory(ReadChunk), cancellationToken);
            if (read == 0)
                break;

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
                return (null, true);
        }

        try
        {
            return (JsonDocument.Parse(buffer.WrittenMemory), false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }
}
