using System.Text;

namespace SyncLane.Cli.Curl;

/// <summary>
/// A curl command as people copy it: from API documentation, from a browser's "Copy as cURL" (bash or cmd), or written
/// by hand over several lines. Only what a request needs is kept; every option that was not used is listed, so nothing
/// is dropped silently.
/// </summary>
internal sealed record CurlCommand(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    string? User,
    IReadOnlyList<string> Ignored)
{
    private static readonly HashSet<string> Switches =
    [
        "--compressed", "-L", "--location", "-k", "--insecure", "-s", "--silent", "-S", "--show-error", "-v", "--verbose",
        "-i", "--include", "-g", "--globoff", "-f", "--fail", "--fail-with-body", "--http1.1", "--http2", "--http3", "-N", "--no-buffer"
    ];

    private static readonly HashSet<string> IgnoredWithValue =
    [
        "-o", "--output", "--connect-timeout", "-m", "--max-time", "--retry", "-w", "--write-out", "-e", "--referer", "-x", "--proxy"
    ];

    /// <summary>Reads <paramref name="command"/>, or says why it is not a curl command a recipe can come from.</summary>
    public static SyncResult<CurlCommand> Parse(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Parse(Words(command));
    }

    /// <summary>Reads a curl command whose words a shell has already split, as they arrive after <c>synclane init</c>.</summary>
    public static SyncResult<CurlCommand> Parse(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var words = command.ToList();
        if (words.Count > 0 && words[0] == "curl")
            words.RemoveAt(0);

        string? method = null, url = null, body = null, user = null;
        var headers = new List<KeyValuePair<string, string>>();
        var ignored = new List<string>();
        var get = false;
        var data = new List<string>();

        // Sequential: options are read in order, each taking the words that follow it.
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            string Next() => index + 1 < words.Count ? words[++index] : "";

            switch (word)
            {
                case "-X" or "--request":
                    method = Next().ToUpperInvariant();
                    break;
                case "-H" or "--header":
                    var header = Next();
                    var colon = header.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                        headers.Add(new(header[..colon].Trim(), header[(colon + 1)..].Trim()));
                    break;
                case "-d" or "--data" or "--data-raw" or "--data-binary" or "--data-ascii":
                    data.Add(Next());
                    break;
                case "--data-urlencode":
                    data.Add(UrlEncoded(Next()));
                    break;
                case "--json":
                    data.Add(Next());
                    headers.Add(new("Content-Type", "application/json"));
                    headers.Add(new("Accept", "application/json"));
                    break;
                case "-G" or "--get":
                    get = true;
                    break;
                case "-u" or "--user":
                    user = Next();
                    break;
                case "--oauth2-bearer":
                    headers.Add(new("Authorization", $"Bearer {Next()}"));
                    break;
                case "-A" or "--user-agent":
                    Next();
                    ignored.Add($"{word} (SyncLane sends its own User-Agent)");
                    break;
                case "-b" or "--cookie":
                    Next();
                    ignored.Add($"{word} (cookies are not kept: they usually hold a session)");
                    break;
                case "--url":
                    url = Next();
                    break;
                default:
                    if (Switches.Contains(word))
                    {
                        ignored.Add(word);
                    }
                    else if (IgnoredWithValue.Contains(word))
                    {
                        Next();
                        ignored.Add(word);
                    }
                    else if (word.StartsWith('-'))
                    {
                        ignored.Add(word);
                    }
                    else
                    {
                        url ??= word;
                    }

                    break;
            }
        }

        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return SyncResult<CurlCommand>.Failure(CliErrors.CurlWithoutUrl());

        if (data.Count > 0)
        {
            if (get)
                url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join('&', data);
            else
                body = string.Join('&', data);
        }

        method ??= body is null ? "GET" : "POST";
        return SyncResult<CurlCommand>.Success(new CurlCommand(method, url, headers, body, user, ignored));
    }

    /// <summary>
    /// Splits a command line into words as a shell would: single and double quotes, <c>$'…'</c> with its escapes, a
    /// backslash before a line break as a continuation, and, for commands copied for Windows' cmd, <c>^</c> as the escape.
    /// </summary>
    internal static List<string> Words(string command)
    {
        var cmd = command.Contains("^\"", StringComparison.Ordinal) || command.Contains(" ^\n", StringComparison.Ordinal) || command.Contains(" ^\r\n", StringComparison.Ordinal);
        var escape = cmd ? '^' : '\\';
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;

        // Sequential: one character after another; quotes change how the next ones are read.
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];

            // Windows' "Copy as cURL (cmd)" writes ^" where a shell would write ": the quote that opens or closes an argument.
            if (cmd && character == '^' && index + 1 < command.Length && command[index + 1] == '"')
            {
                index = DoubleQuoted(command, index + 2, word, escape);
                inWord = true;
            }
            else if (character == escape && index + 1 < command.Length)
            {
                var next = command[index + 1];
                index++;
                if (next is '\n')
                    continue;

                if (next is '\r' && index + 1 < command.Length && command[index + 1] == '\n')
                {
                    index++;
                    continue;
                }

                word.Append(next);
                inWord = true;
            }
            else if (!cmd && character == '$' && index + 1 < command.Length && command[index + 1] == '\'')
            {
                index = AnsiQuoted(command, index + 2, word);
                inWord = true;
            }
            else if (!cmd && character == '\'')
            {
                var end = command.IndexOf('\'', index + 1);
                end = end < 0 ? command.Length : end;
                word.Append(command, index + 1, end - index - 1);
                index = end;
                inWord = true;
            }
            else if (character == '"')
            {
                index = DoubleQuoted(command, index + 1, word, escape);
                inWord = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
            }
            else
            {
                word.Append(character);
                inWord = true;
            }
        }

        if (inWord)
            words.Add(word.ToString());

        return words;
    }

    private static int DoubleQuoted(string command, int start, StringBuilder word, char escape)
    {
        // Sequential: one character after another, until the closing quote.
        for (var index = start; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '"')
                return index;

            if (escape == '^' && character == '^' && index + 1 < command.Length && command[index + 1] == '"')
                return index + 1;

            if (character == escape && index + 1 < command.Length && (escape == '^' || command[index + 1] is '"' or '\\' or '$' or '`' or '\n'))
            {
                index++;
                if (command[index] != '\n')
                    word.Append(command[index]);

                continue;
            }

            word.Append(character);
        }

        return command.Length;
    }

    private static int AnsiQuoted(string command, int start, StringBuilder word)
    {
        // Sequential: one character after another, until the closing quote.
        for (var index = start; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '\'')
                return index;

            if (character == '\\' && index + 1 < command.Length)
            {
                index++;
                word.Append(command[index] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => command[index]
                });
                continue;
            }

            word.Append(character);
        }

        return command.Length;
    }

    private static string UrlEncoded(string value)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? Uri.EscapeDataString(value) : $"{value[..equals]}={Uri.EscapeDataString(value[(equals + 1)..])}";
    }
}
