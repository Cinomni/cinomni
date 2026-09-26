using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Host.Tests;

/// <summary>
/// No outgoing request is logged with its URL. In this product a URL is a credential more often than not
/// — a Discord webhook carries its token in the path, an indexer its API key in the query — and the
/// client factory's default handlers used to write every one of them at Information.
/// </summary>
public sealed class HttpClientLoggingTests
{
    [Fact]
    public async Task An_outgoing_request_leaves_no_url_in_the_log()
    {
        // Arrange — the production composition, a sink for every log entry, and a client whose transport
        // answers locally so nothing leaves the test.
        var sink = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        services.AddCinomniModules(new ConfigurationBuilder().Build(), HostComposition.UnusableConnectionString);
        services.AddHttpClient("webhook-probe").ConfigurePrimaryHttpMessageHandler(() => new AnswersOk());
        await using var provider = services.BuildServiceProvider();

        // Act
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("webhook-probe");
        using var response = await client.PostAsync(
            "https://discord.example/api/webhooks/123/super-secret-token", new StringContent("{}"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(sink.Messages, message => message.Contains("super-secret-token", StringComparison.Ordinal));
    }

    private sealed class AnswersOk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(Messages);

        public void Dispose()
        {
        }

        private sealed class Capturing(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
