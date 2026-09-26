using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Captures the SQL EF Core executes by listening on the <c>Microsoft.EntityFrameworkCore.Database.Command</c>
/// log category. It exists so a test can assert on the <i>shape</i> of a read — specifically that the movie
/// snapshot path never touches <c>metadata_episodes</c>, which no assertion over the returned object graph
/// can prove.
/// </summary>
internal sealed class SqlCapture
{
    private const string CommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    private readonly List<string> _statements = [];
    private readonly Lock _gate = new();

    /// <summary>Registers the capture and its logger provider on a test host.</summary>
    public static SqlCapture Register(IServiceCollection services)
    {
        var capture = new SqlCapture();
        services.AddSingleton(capture);
        services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(capture));
        return capture;
    }

    /// <summary>Drops everything captured so far, so the next assertion only sees one operation.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _statements.Clear();
        }
    }

    /// <summary>Every SQL statement captured since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<string> Statements
    {
        get
        {
            lock (_gate)
            {
                return [.. _statements];
            }
        }
    }

    /// <summary>Whether any captured statement mentions the given table.</summary>
    public bool Touched(string table) =>
        Statements.Any(sql => sql.Contains(table, StringComparison.OrdinalIgnoreCase));

    private void Add(string statement)
    {
        lock (_gate)
        {
            _statements.Add(statement);
        }
    }

    private sealed class CapturingLoggerProvider(SqlCapture capture) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName == CommandCategory ? new CapturingLogger(capture) : NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(SqlCapture capture) : ILogger
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
            capture.Add(formatter(state, exception));
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
