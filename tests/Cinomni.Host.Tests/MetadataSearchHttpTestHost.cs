using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Cinomni.Identity;
using Cinomni.Identity.Persistence;
using Cinomni.Metadata;
using Cinomni.Metadata.Api;
using Cinomni.Metadata.Providers;
using Cinomni.Operations;
using Cinomni.Operations.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// Boots the real Metadata HTTP surface over a real PostgreSQL database with the <b>production</b>
/// provider composition: <c>AddConfiguredMetadataAdapters</c>, binding <c>Metadata:Tmdb:ApiKey</c> out of
/// configuration exactly as a packaged installation does. The search route is therefore answered by the
/// real <see cref="TmdbMetadataSource"/> and not by a stand-in.
/// <para>
/// That is the whole point. Every other metadata suite registers an in-memory <c>IMetadataSource</c>, so
/// an installation with no key configured — the state every installation starts in — could answer an
/// empty list indistinguishable from "that film does not exist" and no test anywhere noticed.
/// </para>
/// <para>
/// The only production part replaced is the socket: the TMDB typed client's primary handler becomes a
/// canned transport, so a test can script what the provider answers without the suite ever reaching the
/// internet. The adapter, the composition, the routing, the authentication and the database are real.
/// </para>
/// </summary>
internal static class MetadataSearchHttpTestHost
{
    public static string ConnectionStringFor(string database) =>
        $"Host=localhost;Port=5442;Database={database};Username=cinomni;Password=cinomni_dev";

    /// <param name="database">A database name unique to the calling test class.</param>
    /// <param name="tmdbTransport">Canned transport for the TMDB typed client — no socket is ever opened.</param>
    /// <param name="logs">Receives every line the installation emits, so a test can assert what it said.</param>
    /// <param name="tmdbApiKey">
    /// The configured key. Empty is the state a freshly installed Cinomni starts in, and is the case the
    /// defect lived in.
    /// </param>
    public static async Task<WebApplication> StartAsync(
        string database,
        HttpMessageHandler tmdbTransport,
        RecordedLogs logs,
        string tmdbApiKey = "")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Metadata:Tmdb:ApiKey"] = tmdbApiKey })
            .Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        builder.Services.AddOperations(ConnectionStringFor(database));
        builder.Services.AddIdentityModule();
        builder.Services.AddIdentityAuthentication();
        builder.Services.AddMetadataModule();
        builder.Services.AddConfiguredMetadataAdapters(configuration, new RetentionConfiguration(configuration));

        // Replaces the transport of the TMDB typed client and nothing else: the adapter resolved from the
        // container is still the production one, configured by the production binder.
        builder.Services.AddHttpClient<TmdbMetadataSource>().ConfigurePrimaryHttpMessageHandler(() => tmdbTransport);

        var app = builder.Build();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            await operations.Database.EnsureDeletedAsync();
            await operations.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
        }

        await app.Services.MigrateMetadataAsync();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMetadataEndpoints();

        await app.StartAsync();
        return app;
    }
}

/// <summary>
/// Answers a request whose path and query contains a registered fragment with the scripted body, and
/// everything else with the fallback, recording what was asked. A provider that is not configured must
/// not reach this at all, so the recorded list is an assertion in its own right.
/// </summary>
internal sealed class CannedHttpTransport(string fallbackBody) : HttpMessageHandler
{
    private readonly List<(string Match, string Body)> _routes = [];

    /// <summary>Every path and query the adapter issued.</summary>
    public ConcurrentBag<string> Requests { get; } = [];

    /// <summary>Answers any request whose path and query contains <paramref name="match"/> with JSON.</summary>
    public CannedHttpTransport Respond(string match, string json)
    {
        _routes.Add((match, json));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
        Requests.Add(pathAndQuery);

        var body = _routes
            .Where(route => pathAndQuery.Contains(route.Match, StringComparison.Ordinal))
            .Select(route => route.Body)
            .FirstOrDefault() ?? fallbackBody;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// Captures what the installation says. A provider that switches itself off leaves no row and fails
/// nothing, so the log line is the only observable evidence that it did.
/// </summary>
internal sealed class RecordedLogs : ILoggerProvider
{
    private readonly ConcurrentBag<(LogLevel Level, string Message)> _records = [];

    public IReadOnlyList<string> WarningsContaining(string fragment) =>
        _records
            .Where(record => record.Level >= LogLevel.Warning
                && record.Message.Contains(fragment, StringComparison.Ordinal))
            .Select(record => record.Message)
            .ToList();

    public ILogger CreateLogger(string categoryName) => new Recorder(_records);

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentBag<(LogLevel Level, string Message)> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                records.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
