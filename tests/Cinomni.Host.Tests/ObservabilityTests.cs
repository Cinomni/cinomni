using System.Diagnostics;
using Cinomni.Host.Observability;
using Cinomni.Kernel.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Cinomni.Host.Tests;

/// <summary>
/// Telemetry is opt-in, and these keep it that way. The failure this guards against is the quiet one:
/// a self-hosted installation that starts sending spans somewhere because a default said so rather than
/// because an operator decided. It also pins the redaction, because the two attributes the automatic
/// HTTP instrumentation exports are both credentials in this product.
/// </summary>
public sealed class ObservabilityTests
{
    [Fact]
    public void With_nothing_configured_no_exporter_is_registered_and_no_span_is_created()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var options = builder.AddCinomniObservability();

        Assert.False(options.IsExportEnabled);

        using var app = builder.Build();

        // No OpenTelemetry provider means no listener, which means the activity source produces
        // nothing. This is what makes the default installation cost nothing and, more to the point,
        // send nothing.
        Assert.Null(app.Services.GetService<TracerProvider>());
        Assert.Null(CinomniTelemetry.Source.StartActivity("must-be-null"));
    }

    [Fact]
    public void Configuring_an_endpoint_is_what_turns_export_on()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Otlp:Endpoint"] = "http://127.0.0.1:4317",
        });

        var options = builder.AddCinomniObservability();

        Assert.True(options.IsExportEnabled);

        using var app = builder.Build();
        Assert.NotNull(app.Services.GetService<TracerProvider>());
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://collector:4317")]
    [InlineData("localhost:4317")]
    public void An_endpoint_that_cannot_be_honoured_stops_startup_instead_of_exporting_nowhere(string endpoint)
    {
        // An operator who asked for telemetry and silently got none is worse off than one whose process
        // refused to start and named the setting — the same reasoning as the missing connection string.
        var failure = Assert.Throws<InvalidOperationException>(() => Read(("Observability:Otlp:Endpoint", endpoint)));

        Assert.Contains("Observability:Otlp:Endpoint", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-0.5")]
    [InlineData("1.5")]
    public void A_sample_ratio_outside_the_unit_interval_is_refused(string ratio)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => Read(("Observability:Traces:SampleRatio", ratio)));

        Assert.Contains("SampleRatio", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_protocol_is_refused_rather_than_silently_defaulted()
    {
        Assert.Throws<InvalidOperationException>(() => Read(
            ("Observability:Otlp:Endpoint", "http://127.0.0.1:4318"),
            ("Observability:Otlp:Protocol", "thrift")));
    }

    [Fact]
    public void The_shipped_defaults_are_export_off_and_a_usable_readiness_budget()
    {
        var options = Read();

        Assert.False(options.IsExportEnabled);
        Assert.Equal("cinomni", options.ServiceName);
        Assert.Equal(1.0, options.TraceSampleRatio);
        Assert.False(options.JsonLogs);
        Assert.True(options.ReadinessTimeout > TimeSpan.Zero);
        Assert.True(options.MinFreeDiskBytes > 0);
    }

    [Fact]
    public void A_media_request_span_loses_its_query_string_because_that_is_the_bearer_token()
    {
        // A playback stream read authenticates with ?access_token=..., because a <video> element cannot
        // set a header. That makes such a server span's query string a credential.
        using var activity = new Activity("server").Start();
        activity.SetTag("url.path", "/api/playback/sessions/abc/stream");
        activity.SetTag("url.query", "?access_token=super-secret-token");

        TelemetryRedaction.RedactServerRequest(activity);

        Assert.Null(activity.GetTagItem("url.query"));
        // The path survives: it is what makes the span useful and it carries nothing private.
        Assert.Equal("/api/playback/sessions/abc/stream", activity.GetTagItem("url.path"));
    }

    [Fact]
    public void An_outgoing_provider_span_keeps_the_path_and_loses_the_key_the_term_and_the_host()
    {
        using var activity = new Activity("client").Start();
        activity.SetTag("url.full", "https://indexer.example/api?t=search&apikey=abcdef123&q=title");
        activity.SetTag("server.address", "indexer.example");
        activity.SetTag("server.port", 443);

        TelemetryRedaction.RedactClientRequest(
            activity, new Uri("https://indexer.example/api?t=search&apikey=abcdef123&q=title"));

        var recorded = Assert.IsType<string>(activity.GetTagItem("url.full"));
        Assert.Equal("/api", recorded);
        Assert.DoesNotContain("apikey", recorded, StringComparison.OrdinalIgnoreCase);
        // The search term goes too. It is what the household is looking for, and it is not the
        // operator's collector's business.
        Assert.DoesNotContain("title", recorded, StringComparison.OrdinalIgnoreCase);

        // And the host. A private tracker's name is the most identifying non-credential value a
        // household installation holds, and for a notification webhook it is a target the user supplied.
        Assert.Null(activity.GetTagItem("server.address"));
        Assert.Null(activity.GetTagItem("server.port"));
    }

    [Fact]
    public void An_incoming_request_span_also_loses_the_host_the_caller_asked_for()
    {
        using var activity = new Activity("server").Start();
        activity.SetTag("server.address", "cinomni.somebodys-house.example");
        activity.SetTag("server.port", 8080);

        TelemetryRedaction.RedactServerRequest(activity);

        // It is this installation's own address, and it comes from a header the caller controls.
        Assert.Null(activity.GetTagItem("server.address"));
        Assert.Null(activity.GetTagItem("server.port"));
    }

    /// <summary>
    /// The promise <c>.env.example</c> and DEPLOYMENT.md make an operator — no path, no info hash, no
    /// title, no search term — has to hold for a log record too, not only for a span. This drives the
    /// very configuration the Host installs, with a second processor standing where the exporter stands.
    /// </summary>
    [Fact]
    public void An_exported_log_record_carries_the_template_and_none_of_the_values_it_was_given()
    {
        const string libraryRoot = "/srv/media/library";
        const string mediaPath = "/srv/media/library/Some Title (2024)/Some Title (2024).mkv";
        const string infoHash = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
        const string searchTerm = "some title 2024";

        var captured = Export(logger =>
        {
            logger.LogInformation(
                "Import left {Path} unresolved under {Root}.", mediaPath, libraryRoot);
            logger.LogInformation(
                "Attempt selected info-hash {InfoHash} for search {Term}.", infoHash, searchTerm);
            logger.LogWarning(
                new InvalidOperationException($"ffmpeg exited 1: -i {mediaPath} -c:v libx264"),
                "Transcode failed to start for session {SessionId}.",
                Guid.NewGuid());
        });

        Assert.Equal(3, captured.Count);

        foreach (var record in captured)
        {
            var everything = record.Text();
            Assert.DoesNotContain(libraryRoot, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(mediaPath, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(infoHash, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(searchTerm, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ffmpeg", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("libx264", everything, StringComparison.OrdinalIgnoreCase);
        }

        // What does travel: the template, with its placeholders intact. An operator sees which code path
        // fired, how often, and in which trace — and reads the values on the console of the installation
        // that produced them.
        Assert.Equal("Import left {Path} unresolved under {Root}.", captured[0].Body);
        Assert.Null(captured[0].FormattedMessage);

        // An exception is reduced to its type. The type says what went wrong; the message and the stack
        // trace say what it was given, and an FFmpeg failure quotes the media path and the argument list.
        Assert.Null(captured[2].Exception);
        var exceptionType = Assert.Single(
            captured[2].Attributes,
            attribute => attribute.Key == LogRecordRedactionProcessor.ExceptionTypeKey);
        Assert.Equal(typeof(InvalidOperationException).FullName, exceptionType.Value);
    }

    [Fact]
    public void A_log_record_keeps_only_the_closed_tag_vocabulary()
    {
        var captured = Export(logger => logger.LogInformation(
            "Indexer search finished as {cinomni.outcome} for {cinomni.indexer.name} with {Term}.",
            CinomniTelemetry.Outcomes.Completed,
            "the operator's indexer",
            "a search term nobody else needs"));

        var record = Assert.Single(captured);
        var keys = record.Attributes.Select(attribute => attribute.Key).ToArray();

        Assert.Contains(CinomniTelemetry.Tags.Outcome, keys);
        Assert.Contains(CinomniTelemetry.Tags.IndexerName, keys);
        Assert.Contains(LogRecordRedactionProcessor.OriginalFormatKey, keys);
        // Everything a call site invented is dropped, so a new log message is safe to export without its
        // author having to know this file exists.
        Assert.DoesNotContain("Term", keys);
        Assert.DoesNotContain("a search term nobody else needs", record.Text(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback that has to be safe rather than useful: a log call with no message template at all.
    /// The SDK renders it, so there is no half of it that can be published — the body goes rather than
    /// the values.
    /// </summary>
    [Fact]
    public void A_log_record_with_no_template_exports_no_body_at_all()
    {
        const string secret = "/srv/media/library/Some Title (2024).mkv";

        var captured = Export(logger => logger.Log(
            LogLevel.Information,
            new EventId(1, "NoTemplate"),
            state: $"Import left {secret} unresolved.",
            exception: null,
            formatter: (state, _) => state));

        var record = Assert.Single(captured);

        Assert.Null(record.Body);
        Assert.Null(record.FormattedMessage);
        Assert.DoesNotContain(secret, record.Text(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs log calls through the Host's own log pipeline and snapshots what reaches an exporter.</summary>
    private static List<CapturedRecord> Export(Action<ILogger> write)
    {
        var captured = new List<CapturedRecord>();

        using (var loggerFactory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(otel =>
        {
            ObservabilityRegistration.ConfigureLogRedaction(otel);

            // Stands exactly where the OTLP exporter's batch processor stands: after the redaction.
            otel.AddProcessor(new CapturingProcessor(captured));
        })))
        {
            write(loggerFactory.CreateLogger("Cinomni.Tests"));
        }

        return captured;
    }

    /// <summary>
    /// A copy of what an exporter would see. Taken eagerly because the SDK reuses <see cref="LogRecord"/>
    /// instances once the pipeline returns.
    /// </summary>
    private sealed record CapturedRecord(
        string? Body,
        string? FormattedMessage,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> Attributes)
    {
        /// <summary>Every exported character of the record, for a leak assertion that cannot miss a field.</summary>
        public string Text() =>
            string.Join(
                '\n',
                [
                    Body,
                    FormattedMessage,
                    Exception?.ToString(),
                    .. Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"),
                ]);
    }

    private sealed class CapturingProcessor(List<CapturedRecord> sink) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data) => sink.Add(new CapturedRecord(
            data.Body,
            data.FormattedMessage,
            data.Exception,
            data.Attributes?.ToArray() ?? []));
    }

    private static ObservabilityOptions Read(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key,
                setting => (string?)setting.Value,
                StringComparer.Ordinal))
            .Build();

        return ObservabilityOptions.Read(configuration);
    }
}
