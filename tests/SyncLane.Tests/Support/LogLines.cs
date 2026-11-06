using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyncLane.Tests.Support;

/// <summary>Keeps every log line written, with its level, for tests that check what an operator is told.</summary>
public sealed class LogLines : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();

    public ILogger<T> For<T>() => new LoggerFactory([this]).CreateLogger<T>();

    /// <summary>Waits until a line at <paramref name="level"/> contains <paramref name="text"/>, and returns it.</summary>
    public async Task<string> WaitForAsync(LogLevel level, string text, TimeSpan patience)
    {
        var started = DateTime.UtcNow;

        // Sequential: polling the lines until one matches.
        while (DateTime.UtcNow - started < patience)
        {
            if (Lines.ToArray().FirstOrDefault(line => line.Level == level && line.Text.Contains(text, StringComparison.Ordinal)) is { Text: { } found })
                return found;

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"No {level} line containing '{text}' within {patience}. Lines seen:\n" +
            string.Join('\n', Lines.ToArray().Select(line => $"{line.Level}: {line.Text}")));
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(LogLines lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
