using Cinomni.Acquisition.Api;
using Cinomni.Catalog.Api;
using Cinomni.Decision.Api;
using Cinomni.Discovery.Api;
using Cinomni.Downloads.Api;
using Cinomni.Identity.Api;
using Cinomni.Import.Api;
using Cinomni.Library.Api;
using Cinomni.Metadata.Api;
using Cinomni.Host.Health;
using Cinomni.Monitoring.Api;
using Cinomni.Notifications.Api;
using Cinomni.Host.Operations;
using Cinomni.Playback.Api;
using Cinomni.RealTime.Api;
using Cinomni.ReleaseParsing.Api;
using Cinomni.Requests.Api;
using Cinomni.Host.SystemInfo;
using Cinomni.Subtitles.Api;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Host;

/// <summary>
/// The HTTP surface of the application, in one place. <c>Program</c> composes the modules and then calls
/// this; the API-authorization test calls the very same method, so what it enumerates is what the Host
/// actually serves — a route added here cannot escape the classification the test enforces.
/// </summary>
public static class CinomniApi
{
    public static IEndpointRouteBuilder MapCinomniEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The probes. All three are deliberately anonymous, so an orchestrator can reach them without
        // credentials, and all three answer through HealthResponse, which emits a status and a check
        // name and nothing else — no description, no exception, no path, no address.

        // Liveness: "is this process alive", answered by the process being able to answer. It runs no
        // check at all, and that is the whole point. The single endpoint this replaces pinged
        // PostgreSQL, so a database outage — a restart, a failover, a full disk on the database host —
        // failed the probe an orchestrator restarts on, and the installation was killed and killed
        // again for a fault no restart can fix. Liveness means restart me; readiness means do not route
        // to me yet, and only the second of those is a dependency question.
        //
        // What this does NOT cover is startup. Program applies sixteen schemas' migrations before
        // app.Run(), so during them nothing is listening and every probe is refused rather than
        // answered; the container HEALTHCHECK's start period is what carries that today. Registration
        // order cannot fix it — the framework's own web host is the LAST hosted service in a
        // WebApplication, so anything registered here still runs before Kestrel binds. Answering during
        // a long migration means running it after startup and holding every module's background worker
        // behind a barrier until it finishes, which is a startup-sequence decision, not a routing one.
        endpoints.MapProbe("/health/live", AliveAsync);

        // Readiness: every dependency this installation needs before it is worth routing to. The answer
        // is memoized for a few seconds, because these endpoints take no credentials and the work behind
        // them — a pooled database connection and an outbound connect — must not be something an
        // anonymous caller can multiply by sending more requests.
        endpoints.MapProbe("/health/ready", ReadyAsync);

        // /health stays, permanently, as an alias of readiness. It is what the Vite dev proxy forwards,
        // what CONTRIBUTING documents, what the smoke script calls and what the authorization test pins
        // — and it always meant readiness, whatever its comment used to say. Removing it would break
        // every one of those for a rename.
        endpoints.MapProbe("/health", ReadyAsync);

        // What this installation is running. Signed in, no administrator gate, and not on /health:
        // see SystemEndpoints for why each of those three is the way it is.
        endpoints.MapSystemEndpoints();

        endpoints.MapIdentityEndpoints();
        endpoints.MapCatalogEndpoints();
        endpoints.MapMetadataEndpoints();
        endpoints.MapMonitoringEndpoints();
        endpoints.MapOperationsEndpoints();
        endpoints.MapDiscoveryEndpoints();
        endpoints.MapReleaseParsingEndpoints();
        endpoints.MapDecisionEndpoints();
        endpoints.MapAcquisitionEndpoints();
        endpoints.MapDownloadEndpoints();
        endpoints.MapImportEndpoints();
        endpoints.MapLibraryEndpoints();
        endpoints.MapPlaybackEndpoints();
        endpoints.MapPlaybackHardwareEndpoints();
        endpoints.MapSubtitleEndpoints();
        endpoints.MapRequestEndpoints();
        endpoints.MapNotificationEndpoints();
        endpoints.MapRealtimeEndpoints();

        // Terminates an unknown API path with the platform's error envelope. Without it the SPA shell
        // below would answer, and the web client would parse HTML as JSON and raise a syntax error
        // instead of the API error every caller already handles. A literal segment outranks a catch-all,
        // so this wins over the shell for anything under /api. Anonymous: it carries no data, only the
        // fact that the route does not exist — which an unauthenticated caller learns from a 404 anyway.
        // It answers every method on purpose, and that costs one thing worth stating: a real route called
        // with a method it does not map now answers 404 rather than 405, because this candidate matches
        // the method and the framework stops looking for a method mismatch to report. The envelope is the
        // same either way, and "no such route" is the more useful answer to a client that guessed a path.
        endpoints.MapFallback(
            "/api/{**slug}",
            () => Results.NotFound(new { error = "api.route_not_found", message = "Unknown API route." }));

        // The web client is a single page with real routes (/works/{id}, /watch/{assetId}), so a reload
        // or a shared deep link must be answered with the shell and routed in the browser. Anonymous by
        // design: index.html is a public shell that renders the sign-in view until a session exists, and
        // every byte of data behind it still goes through the authorized /api surface. It carries the same
        // caching rules as the static middleware, so a deep link cannot outlive an upgrade of the bundle.
        endpoints.MapFallbackToFile("index.html", WebClientFiles.Options());

        return endpoints;
    }

    /// <summary>Answers liveness with a constant: no check, no service, nothing that can be unavailable.</summary>
    private static Task AliveAsync(HttpContext context) =>
        HealthResponse.WriteAsync(context, ReadinessSnapshot.Alive);

    /// <summary>Answers readiness from the shared snapshot rather than by running the checks again.</summary>
    private static async Task ReadyAsync(HttpContext context, ReadinessSnapshot readiness) =>
        await HealthResponse.WriteAsync(context, await readiness.ReadAsync(context.RequestAborted));

    /// <summary>
    /// Maps one probe: anonymous, GET only, and answered by the redacting writer.
    /// <para>
    /// Constrained to GET rather than left on the framework's every-method default, so the surface the
    /// authorization test enumerates keeps saying exactly which verb each probe answers. A probe is a
    /// read; a POST to it should 405, not report health.
    /// </para>
    /// </summary>
    private static void MapProbe(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) =>
        endpoints.MapGet(pattern, handler);
}
