namespace Cinomni.Host.Observability;

/// <summary>How an OTLP endpoint is spoken to.</summary>
internal enum OtlpProtocol
{
    /// <summary>gRPC on port 4317 — the collector default.</summary>
    Grpc,

    /// <summary>HTTP/protobuf on port 4318, for a collector behind a proxy that will not carry h2c.</summary>
    HttpProtobuf,
}

/// <summary>
/// The <c>Observability</c> configuration section, read once at startup.
/// <para>
/// <b>Everything here is off by default.</b> With no endpoint configured nothing is registered, no
/// listener attaches to the activity source, and no exporter runs — so an installation that never asks
/// for telemetry does not pay for it and, more importantly, never sends anything anywhere. Telemetry
/// leaving a self-hosted box has to be a decision somebody made, not a default somebody has to find and
/// switch off.
/// </para>
/// </summary>
internal sealed class ObservabilityOptions
{
    /// <summary>Five gibibytes: enough for a transcode session plus a large import to land.</summary>
    public const long DefaultMinFreeDiskBytes = 5L * 1024 * 1024 * 1024;

    /// <summary><c>service.name</c> on every exported span, metric and log record.</summary>
    public string ServiceName { get; init; } = "cinomni";

    /// <summary>
    /// The collector to export to. Empty — the default — means telemetry is disabled entirely.
    /// <para>
    /// This is <b>operator configuration</b> and is deliberately exempt from <c>SsrfGuard</c>: a
    /// collector lives on loopback or on a private container network, which is precisely what the guard
    /// exists to refuse. That is only safe because the value can never be set through the API — it comes
    /// from appsettings or the environment. If a settings endpoint ever exposes it, this becomes an SSRF
    /// hole and the guard has to come back.
    /// </para>
    /// </summary>
    public string OtlpEndpoint { get; init; } = string.Empty;

    /// <summary>Wire protocol for <see cref="OtlpEndpoint"/>.</summary>
    public OtlpProtocol OtlpProtocol { get; init; } = OtlpProtocol.Grpc;

    /// <summary>
    /// Fraction of traces to keep, 0.0 to 1.0. Sampling is parent-based, so a decision taken at the
    /// start of an acquisition is honoured across the outbox and the command queue — half a trace is
    /// worse than none.
    /// </summary>
    public double TraceSampleRatio { get; init; } = 1.0;

    /// <summary>
    /// Emit log records as JSON. Independent of the exporter: an installation shipping logs with a file
    /// collector wants this without wanting OTLP, and correlation ids are attached either way.
    /// </summary>
    public bool JsonLogs { get; init; }

    /// <summary>
    /// Free space below which a storage root reports degraded on the readiness probe. Below this an
    /// import has nowhere to land and a transcode dies mid-session, and both are far cheaper to see
    /// coming than to diagnose afterwards.
    /// </summary>
    public long MinFreeDiskBytes { get; init; } = DefaultMinFreeDiskBytes;

    /// <summary>
    /// Budget for one readiness dependency check. A probe that can hang is a probe an orchestrator
    /// times out on, which reads as "unhealthy" without saying which dependency was slow.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Whether anything is exported at all.</summary>
    public bool IsExportEnabled => OtlpEndpoint.Length > 0;

    /// <summary>
    /// Reads the section explicitly, key by key — never a blanket <c>Bind</c>, exactly as
    /// <see cref="ModuleConfiguration"/> does and for the same reason: a typo then leaves the default in
    /// place instead of silently reshaping the runtime.
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured value cannot be honoured.</exception>
    public static ObservabilityOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Observability");
        var otlp = section.GetSection("Otlp");

        var options = new ObservabilityOptions
        {
            ServiceName = Text(section, "ServiceName") ?? "cinomni",
            OtlpEndpoint = Text(otlp, "Endpoint") ?? string.Empty,
            OtlpProtocol = ParseProtocol(Text(otlp, "Protocol")),
            TraceSampleRatio = section.GetSection("Traces").GetValue<double?>("SampleRatio") ?? 1.0,
            JsonLogs = section.GetSection("Logs").GetValue("Json", false),
            MinFreeDiskBytes = section.GetSection("Readiness")
                .GetValue<long?>("MinFreeDiskBytes") ?? DefaultMinFreeDiskBytes,
            ReadinessTimeout = section.GetSection("Readiness")
                .GetValue<TimeSpan?>("Timeout") ?? TimeSpan.FromSeconds(3),
        };

        options.Validate();
        return options;
    }

    /// <summary>
    /// Fails startup on a configuration that cannot be honoured, rather than exporting to nowhere. An
    /// operator who asked for telemetry and silently got none is worse off than one whose process
    /// refused to start and said why — the same reasoning as the missing connection string in
    /// <c>Program</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured value cannot be honoured.</exception>
    public void Validate()
    {
        if (OtlpEndpoint.Length > 0)
        {
            if (!Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "Configuration 'Observability:Otlp:Endpoint' must be an absolute http(s) URL pointing at "
                    + "an OpenTelemetry collector, for example 'http://localhost:4317'. Leave it unset to "
                    + "disable telemetry.");
            }
        }

        if (TraceSampleRatio is < 0 or > 1 || double.IsNaN(TraceSampleRatio))
        {
            throw new InvalidOperationException(
                "Configuration 'Observability:Traces:SampleRatio' must be between 0.0 and 1.0.");
        }

        if (MinFreeDiskBytes < 0)
        {
            throw new InvalidOperationException(
                "Configuration 'Observability:Readiness:MinFreeDiskBytes' cannot be negative.");
        }

        if (ReadinessTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Configuration 'Observability:Readiness:Timeout' must be a positive duration.");
        }
    }

    private static OtlpProtocol ParseProtocol(string? value) => value?.Replace("/", string.Empty, StringComparison.Ordinal)
        .Replace("_", string.Empty, StringComparison.Ordinal)
        .Trim()
        .ToLowerInvariant() switch
    {
        null or "" or "grpc" => OtlpProtocol.Grpc,
        "httpprotobuf" or "http" => OtlpProtocol.HttpProtobuf,
        _ => throw new InvalidOperationException(
            "Configuration 'Observability:Otlp:Protocol' must be 'grpc' or 'httpprotobuf'."),
    };

    private static string? Text(IConfiguration section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
