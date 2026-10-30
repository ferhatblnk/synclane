using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SyncLane.Http;

/// <summary>
/// Builds the request of one page from a compiled recipe and a job. Each placeholder value is escaped for where it
/// lands: percent-encoded in the URL, written as a JSON string in the body, refused in a header if it holds a line break.
/// </summary>
internal static class RequestRenderer
{
    private static readonly MediaTypeHeaderValue Json = new("application/json") { CharSet = "utf-8" };

    public static SyncResult<HttpRequestMessage> Render(CompiledRecipe recipe, Job job)
    {
        if (recipe.UsesWindow && (job.WindowStart is null || job.WindowEnd is null))
            return SyncResult<HttpRequestMessage>.Failure(HttpErrors.WindowMissing(recipe.Name));

        string Value(string name) => name switch
        {
            Placeholders.WindowStart => Format(job.WindowStart!.Value, recipe.WindowFormat),
            Placeholders.WindowEnd => Format(job.WindowEnd!.Value, recipe.WindowFormat),
            Placeholders.OwnerKey => job.Owner,
            _ => throw new UnreachableException()
        };

        var request = new HttpRequestMessage(recipe.Method, Url(recipe, Value));

        foreach (var (header, template) in recipe.Headers)
        {
            var value = template.Render(Value);
            if (value.AsSpan().ContainsAny('\r', '\n'))
            {
                request.Dispose();
                return SyncResult<HttpRequestMessage>.Failure(HttpErrors.RenderedValueInvalid(recipe.Name, header));
            }

            request.Headers.TryAddWithoutValidation(header, value);
        }

        if (recipe.Recipe.Request.Body is { } body)
        {
            request.Content = new ByteArrayContent(Body(body, recipe.BodyStrings, Value));
            request.Content.Headers.ContentType = Json;

            foreach (var (header, template) in recipe.Headers.Where(header => header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)))
            {
                request.Content.Headers.Remove(header);
                request.Content.Headers.TryAddWithoutValidation(header, template.Render(Value));
            }
        }

        return SyncResult<HttpRequestMessage>.Success(request);
    }

    public static string Format(DateTimeOffset moment, WindowFormat format)
    {
        var utc = moment.ToUniversalTime();
        return format switch
        {
            WindowFormat.Iso8601 => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            WindowFormat.Unix => utc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            WindowFormat.UnixMilliseconds => utc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            WindowFormat.Date => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => throw new UnreachableException()
        };
    }

    private static Uri Url(CompiledRecipe recipe, Func<string, string> value)
    {
        var url = new StringBuilder(recipe.Url.Render(name => Uri.EscapeDataString(value(name))));
        var separator = url.ToString().Contains('?') ? '&' : '?';

        foreach (var (name, template) in recipe.Query)
        {
            url.Append(separator).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(template.Render(value)));
            separator = '&';
        }

        return new Uri(url.ToString(), UriKind.Absolute);
    }

    private static byte[] Body(JsonElement body, IReadOnlyDictionary<string, Template> strings, Func<string, string> value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, body, strings, value);

        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, IReadOnlyDictionary<string, Template> strings, Func<string, string> value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, strings, value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item, strings, value);

                writer.WriteEndArray();
                break;

            case JsonValueKind.String when strings.TryGetValue(element.GetString()!, out var template):
                writer.WriteStringValue(template.Render(value));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
