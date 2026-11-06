using System.Net;
using System.Text;

namespace SyncLane.Cli;

/// <summary>
/// Everything the command reaches outside itself: the terminal, piped input, the environment, the network, the clock
/// and the current folder. Tests replace each of them; <see cref="FromConsole"/> is the real thing.
/// </summary>
internal sealed class CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required string Directory { get; init; }

    /// <summary>Text piped into the command, or <c>null</c> when its input is a terminal.</summary>
    public TextReader? Piped { get; init; }

    /// <summary>Asks for a value in the terminal without showing it, or <c>null</c> when there is no terminal to ask in.</summary>
    public Func<string, string?>? AskSecret { get; init; }

    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Makes the handler requests go through, or <c>null</c> for the real network.</summary>
    public Func<HttpMessageHandler>? Network { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The same host, working in another folder.</summary>
    public CliHost In(string directory) => new()
    {
        Out = Out,
        Error = Error,
        Directory = directory,
        Piped = Piped,
        AskSecret = AskSecret,
        Environment = Environment,
        Network = Network,
        Time = Time
    };

    public static CliHost FromConsole() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        Directory = System.Environment.CurrentDirectory,
        Piped = Console.IsInputRedirected ? Console.In : null,
        AskSecret = Console.IsInputRedirected ? null : ReadHidden
    };

    /// <summary>
    /// The client of the one request <c>synclane init</c> sends. Like recipes, it follows no redirect: a recipe written
    /// from a redirected answer would fail when its job runs.
    /// </summary>
    public HttpClient ProbeClient() =>
        new(Network?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(100)
        };

    public int Fail(IEnumerable<SyncError> errors)
    {
        foreach (var error in errors)
            Error.WriteLine(error.ToString());

        return 1;
    }

    public int Fail(SyncError error) => Fail([error]);

    private static string? ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var value = new StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                    value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return value.Length == 0 ? null : value.ToString();
    }
}
