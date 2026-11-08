using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace NewStore.Inventory.Tests;

/// <summary>One log call the app made.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>Keeps every log record the app writes, so a test asserts on records and not on console text.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _records = new();

    public IReadOnlyList<CapturedLog> Snapshot() => [.. _records];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _records);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<CapturedLog> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            records.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), exception));
    }
}
