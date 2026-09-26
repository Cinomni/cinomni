using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Captures the formatted log messages the modules emit, so a test can assert that something the
/// platform decided to <em>drop</em> left a trace. A dropped command is otherwise invisible: it writes
/// no row, publishes no event and fails nothing.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Recorder(Messages);

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentBag<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}
