using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Tests;

/// <summary>
/// Captures what the module logged, so a test can assert on the one line an operator is supposed to
/// read — and on what it must never contain. It exists for the copy-instead-of-hardlink warning: the
/// persisted operation type says what happened to one file, and only the log says the installation's
/// storage contract is broken.
/// </summary>
internal sealed class LogCapture
{
    private readonly List<(LogLevel Level, string Message)> _records = [];
    private readonly Lock _gate = new();

    /// <summary>Registers the capture and its provider on a test host.</summary>
    public static LogCapture Register(IServiceCollection services)
    {
        var capture = new LogCapture();
        services.AddSingleton(capture);
        services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(capture));
        return capture;
    }

    /// <summary>Every warning captured so far, in order.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return [.. _records.Where(r => r.Level == LogLevel.Warning).Select(r => r.Message)];
            }
        }
    }

    /// <summary>Drops everything captured so far, so the next assertion only sees one drive.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
        }
    }

    private void Add(LogLevel level, string message)
    {
        lock (_gate)
        {
            _records.Add((level, message));
        }
    }

    private sealed class CapturingLoggerProvider(LogCapture capture) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(capture);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(LogCapture capture) : ILogger
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
            capture.Add(logLevel, formatter(state, exception));
    }
}
