using Microsoft.Extensions.Logging;

namespace NewStore.Gateway.Tests;

/// <summary>Collects log messages instead of printing them. A test that expects a warning with a stack trace uses it to keep the output short.</summary>
internal sealed class LogCollector : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Collecting(this);

    public void Dispose()
    {
    }

    private sealed class Collecting(LogCollector owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._gate)
            {
                owner._entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
