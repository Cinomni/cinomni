using System.Diagnostics.Metrics;
using System.Reflection;
using Cinomni.Kernel.Diagnostics;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Cinomni.Host.SystemInfo;

namespace Cinomni.Host.Observability;

/// <summary>
/// Wires telemetry at the composition root, and only there. A module records through the BCL's own
/// <c>ActivitySource</c> and <c>Meter</c> — free, in the shared framework, and inert with no listener —
/// so nothing below is a dependency any module can see, and instrumentation never becomes a back door
/// between two of them.
/// <para>
/// <b>Off unless asked.</b> With no <c>Observability:Otlp:Endpoint</c> configured, no OpenTelemetry
/// provider is registered at all: no listener attaches, <c>StartActivity</c> keeps returning null, and
/// no bytes leave the process. Telemetry departing a self-hosted installation is a decision an operator
/// makes, never a default they have to discover and switch off.
/// </para>
/// <para>
/// <b>OTLP only.</b> There is no scrape endpoint and no Prometheus exporter, because the one that
/// exists has never shipped a stable release while the rest of the stack has. An installation that
/// wants Prometheus runs an OpenTelemetry Collector, which is the supported topology regardless — and
/// it keeps a metrics surface off the API port, where every route is classified by
/// <c>ApiAuthorizationTests</c>.
/// </para>
/// </summary>
internal static class ObservabilityRegistration
{
    /// <summary>
    /// Reads the <c>Observability</c> section, always turns on log correlation, and registers the
    /// OpenTelemetry providers when — and only when — an endpoint is configured. Returns the options so
    /// the health registration can reuse the readiness settings rather than re-read them.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration cannot be honoured.</exception>
    public static ObservabilityOptions AddCinomniObservability(this WebApplicationBuilder builder)
    {
        var options = ObservabilityOptions.Read(builder.Configuration);

        ConfigureLogCorrelation(builder, options);

        if (!options.IsExportEnabled)
        {
            return options;
        }

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: options.ServiceName,
                serviceVersion: AssemblyVersion(),
                // A random id per process rather than the machine name: it distinguishes restarts and
                // replicas just as well, and it keeps a host name — which is often a person's — out of
                // exported telemetry.
                serviceInstanceId: Guid.NewGuid().ToString("N"));

        builder.Services
            .AddOpenTelemetry()
            .WithTracing(tracing => ConfigureTracing(tracing, options, resource))
            .WithMetrics(metrics => ConfigureMetrics(metrics, options, resource));

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);
            ConfigureLogRedaction(logging);
            logging.AddOtlpExporter(exporter => ConfigureExporter(exporter, options));
        });

        return options;
    }

    /// <summary>
    /// Everything that decides what a log record is allowed to carry off this node. Called by the
    /// registration above and, unchanged, by the test that pins the promise — so what is asserted is what
    /// the Host actually exports.
    /// <para>
    /// The template travels; the rendered message does not. This product's own log messages interpolate
    /// library roots, media paths, info hashes and search terms, and an exception message quotes the
    /// input that produced it, so a log record gets exactly the treatment a span attribute gets. Trace
    /// and span ids are attached from the ambient activity, which is what makes a log line joinable to
    /// its trace and is the reason to export log records at all.
    /// </para>
    /// </summary>
    internal static void ConfigureLogRedaction(OpenTelemetryLoggerOptions logging)
    {
        // Leaves LogRecord.FormattedMessage unset whenever a message template is available, which is
        // every log call in this repository. The processor below removes it in any case.
        logging.IncludeFormattedMessage = false;

        // A scope carries whatever an enclosing frame put in it, including ASP.NET Core's request scope.
        logging.IncludeScopes = false;
        logging.ParseStateValues = false;

        // Registered before the exporter, so it runs before the batch processor hands anything over.
        logging.AddProcessor(new LogRecordRedactionProcessor());
    }

    /// <summary>
    /// Correlation, on in every installation and costing nothing: the BCL puts the ambient trace and
    /// span ids into a logging scope, so a console line can be tied to the trace that produced it even
    /// when nothing is exported. Without this a log and a trace are two unrelated stories about the
    /// same failure.
    /// </summary>
    private static void ConfigureLogCorrelation(WebApplicationBuilder builder, ObservabilityOptions options)
    {
        builder.Logging.Configure(logging => logging.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId
            | ActivityTrackingOptions.SpanId
            | ActivityTrackingOptions.ParentId);

        if (options.JsonLogs)
        {
            // Switches the formatter on the console provider the host already registered; it does not
            // add a second one, so output is never duplicated.
            builder.Logging.AddJsonConsole(console => console.IncludeScopes = true);
            return;
        }

        builder.Logging.AddSimpleConsole(console => console.IncludeScopes = true);
    }

    private static void ConfigureTracing(
        TracerProviderBuilder tracing,
        ObservabilityOptions options,
        ResourceBuilder resource)
    {
        tracing
            .SetResourceBuilder(resource)
            // Parent-based on purpose. A sampling decision is taken once, at the start of an
            // acquisition, and then travels through the outbox and the command queue with the trace
            // context — so a later hop honours it instead of re-rolling. Half a trace is worse than no
            // trace: it looks like the work stopped where the sampler did.
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)))
            .AddSource(CinomniTelemetry.ActivitySourceName)
            // Npgsql publishes its own spans, which is what puts a slow query underneath the command
            // that ran it.
            .AddSource("Npgsql")
            .AddAspNetCoreInstrumentation(instrumentation =>
            {
                // A media request authenticates with a token in the query string, so a server span's
                // query string is a credential. See TelemetryRedaction.
                instrumentation.EnrichWithHttpRequest = (activity, _) =>
                    TelemetryRedaction.RedactServerRequest(activity);

                // An exception message can quote the input that produced it and a stack trace is
                // infrastructure detail. Both belong in a log record this installation controls, not in
                // an exported span.
                instrumentation.RecordException = false;
            })
            .AddHttpClientInstrumentation(instrumentation =>
            {
                // An indexer, metadata or subtitle URL carries the account's API key, and its host names
                // the private tracker or the webhook a user supplied. See TelemetryRedaction.
                instrumentation.EnrichWithHttpRequestMessage = (activity, request) =>
                    TelemetryRedaction.RedactClientRequest(activity, request.RequestUri);

                instrumentation.RecordException = false;
            })
            .AddOtlpExporter(exporter => ConfigureExporter(exporter, options));
    }

    private static void ConfigureMetrics(
        MeterProviderBuilder metrics,
        ObservabilityOptions options,
        ResourceBuilder resource)
    {
        metrics
            .SetResourceBuilder(resource)
            // The product's own instruments: the spine, the guards, and each module's domain numbers.
            .AddMeter(CinomniTelemetry.MeterName)
            // The outbound host is not a dimension. System.Net.Http tags every client instrument with
            // server.address, which for this product is the indexer, metadata provider or user-supplied
            // webhook that was called — the most identifying non-credential value a household
            // installation holds, and unbounded in cardinality besides. What survives answers "is
            // something outbound slow or failing"; "which provider" is answered by the module's own
            // instruments, which use the operator-chosen indexer name.
            .AddView(RestrictHttpClientDimensions)
            // Free built-ins. Between them they answer "is the process healthy" (runtime), "is the API
            // slow" (hosting/Kestrel), "is something outbound slow" (http) and "is a dependency down"
            // (health checks) without a line of instrumentation of our own.
            .AddMeter("Microsoft.AspNetCore.Hosting")
            .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
            .AddMeter("Microsoft.AspNetCore.Routing")
            .AddMeter("Microsoft.Extensions.Diagnostics.HealthChecks")
            .AddMeter("System.Net.Http")
            .AddMeter("System.Runtime")
            .AddMeter("Npgsql")
            .AddOtlpExporter(exporter => ConfigureExporter(exporter, options));
    }

    /// <summary>
    /// The dimensions an outbound HTTP instrument keeps. Everything else — <c>server.address</c>,
    /// <c>server.port</c>, and anything a future runtime adds — is dropped, because a view that names
    /// what it allows cannot silently start exporting a new tag.
    /// </summary>
    private static readonly string[] HttpClientDimensions =
    [
        "http.request.method",
        "url.scheme",
        "http.response.status_code",
        "error.type",
        "network.protocol.version",
    ];

    /// <summary>Applies <see cref="HttpClientDimensions"/> to every <c>System.Net.Http</c> instrument.</summary>
    private static MetricStreamConfiguration? RestrictHttpClientDimensions(Instrument instrument) =>
        instrument.Meter.Name == "System.Net.Http"
            ? new MetricStreamConfiguration { TagKeys = HttpClientDimensions }
            : null;

    private static void ConfigureExporter(OtlpExporterOptions exporter, ObservabilityOptions options)
    {
        exporter.Endpoint = new Uri(options.OtlpEndpoint);
        exporter.Protocol = options.OtlpProtocol == OtlpProtocol.HttpProtobuf
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }

    // The same build identity the /api/system/info route reports. Two readers of the same three
    // attributes would eventually disagree about which one wins, and a trace tagged with a version
    // other than the one an operator can read is worse than no tag at all.
    private static string AssemblyVersion() => BuildInfo.Current.InformationalVersion;
}
