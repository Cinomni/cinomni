using Cinomni.Kernel.Security;
using Cinomni.Playback.Encoding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Playback.Api;

/// <summary>
/// The operator's view of what this host can transcode with: what the probe found and every test it ran,
/// and a way to run it again after changing a driver, a device or the container — without a restart.
/// Administrator only: it names the platform and the FFmpeg build.
/// </summary>
public static class PlaybackHardwareEndpoints
{
    /// <summary>One probe at a time: each runs a dozen short FFmpeg processes against the device.</summary>
    private static readonly SemaphoreSlim Probing = new(1, 1);

    public static IEndpointRouteBuilder MapPlaybackHardwareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/playback/hardware").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", (HardwareCapabilitiesCache cache) => Results.Ok(ToDto(cache.Current)));

        group.MapPost("/probe", async (
            IHardwareCapabilityProbe probe,
            HardwareCapabilitiesCache cache,
            CancellationToken cancellationToken) =>
        {
            if (!await Probing.WaitAsync(TimeSpan.Zero, cancellationToken))
            {
                return Results.Conflict(new { error = "playback.probe_running", message = "A hardware test is already running." });
            }

            try
            {
                var capabilities = await probe.ProbeAsync(cancellationToken);
                cache.Publish(capabilities);
                return Results.Ok(ToDto(capabilities));
            }
            finally
            {
                Probing.Release();
            }
        });

        return endpoints;
    }

    private static object ToDto(HardwareCapabilities capabilities) => new
    {
        platform = capabilities.Report?.Platform,
        ffmpegVersion = capabilities.Report?.FfmpegVersion,
        probedAt = capabilities.Report?.ProbedAt,
        hwaccels = capabilities.Report?.Hwaccels ?? [],
        backends = capabilities.AvailableBackends.Select(backend => new
        {
            backend = backend.ToString(),
            encodes = capabilities.EncodableCodecs.Where(c => c.Backend == backend).Select(c => c.Codec.ToString()),
            decodes = capabilities.DecodeCapableCodecs.Where(c => c.Backend == backend).Select(c => c.Codec),
        }),
        softwareHevc = capabilities.SoftwareHevc,
        toneMapping = capabilities.ToneMapping,
        subtitleOverlay = capabilities.SubtitleOverlay,
        tests = (capabilities.Report?.Tests ?? []).Select(test => new
        {
            backend = test.Backend.ToString(),
            kind = test.Kind.ToString(),
            codec = test.Codec,
            passed = test.Passed,
            failure = test.Failure,
        }),
    };
}
