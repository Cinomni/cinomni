using System.Globalization;
using System.Security.Claims;
using Cinomni.Playback.Application;
using Cinomni.Kernel.Security;
using Cinomni.Playback.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Playback.Api;

/// <summary>
/// HTTP surface of the Playback module: request a session (get the explainable plan + stream URL),
/// report progress, stop, and serve the media — the file itself for Direct Play or the HLS
/// manifest/segments for a transcode. Every route is authenticated; the caller's identity comes from
/// the token, and a session only serves its own user.
/// </summary>
public static class PlaybackEndpoints
{
    /// <summary>Upper bound on a batch progress lookup — a season, generously, not an unbounded scan.</summary>
    private const int MaxProgressAssetsPerRequest = 200;

    /// <summary>"Continue watching" rows returned when the caller does not ask for a number.</summary>
    private const int DefaultInProgress = 20;

    public static IEndpointRouteBuilder MapPlaybackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/playback").RequireAuthorization();

        group.MapPost("/sessions", async (
            RequestPlaybackBody body,
            ClaimsPrincipal principal,
            IPlaybackSessionCommands commands,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var result = await commands.RequestPlaybackAsync(
                viewer, body.AssetId, body.Capability, body.Preferences, cancellationToken);
            if (result.IsFailure)
            {
                var envelope = new { error = result.Error.Code, message = result.Error.Message };
                switch (result.Error.Code)
                {
                    case PlaybackErrors.AssetNotFound:
                        return Results.NotFound(envelope);
                    case PlaybackErrors.TranscodeLimit:
                        // A full node or account is a "not now", not a "never": the same request succeeds
                        // once a stream ends. A stream stopped by its viewer frees its slot at once; one
                        // left behind is reclaimed by the sweep, which runs this often.
                        httpContext.Response.Headers.RetryAfter =
                            ((int)TranscodeReaper.Interval.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                        return Results.Json(envelope, statusCode: StatusCodes.Status429TooManyRequests);
                    default:
                        return Results.UnprocessableEntity(envelope);
                }
            }

            return Results.Ok(ToTicketDto(result.Value));
        });

        group.MapPost("/sessions/{id:guid}/progress", async (
            Guid id,
            ProgressBody body,
            ClaimsPrincipal principal,
            IPlaybackSessionCommands commands,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            await commands.ReportProgressAsync(
                viewer, new PlaybackSessionId(id), body.PositionTicks, body.DurationTicks, body.IsPaused, cancellationToken);
            return Results.NoContent();
        });

        // The viewer switched subtitles in the player; recorded so the title remembers the choice.
        group.MapPut("/sessions/{id:guid}/subtitle", async (
            Guid id,
            SubtitleChoiceBody body,
            ClaimsPrincipal principal,
            IPlaybackSessionCommands commands,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var chosen = await commands.ChooseSubtitleAsync(
                viewer, new PlaybackSessionId(id), body.SubtitleStreamIndex, cancellationToken);
            return chosen ? Results.NoContent() : Results.NotFound();
        });

        group.MapPost("/sessions/{id:guid}/stop", async (
            Guid id,
            ClaimsPrincipal principal,
            IPlaybackSessionCommands commands,
            CancellationToken cancellationToken) =>
        {
            if (CurrentUserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            await commands.StopPlaybackAsync(userId, new PlaybackSessionId(id), cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/sessions/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            IPlaybackQuery query,
            CancellationToken cancellationToken) =>
        {
            if (CurrentUserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var detail = await query.GetSessionAsync(userId, new PlaybackSessionId(id), cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(ToDetailDto(detail));
        });

        group.MapGet("/progress/{assetId:guid}", async (
            Guid assetId,
            ClaimsPrincipal principal,
            IPlaybackQuery query,
            CancellationToken cancellationToken) =>
        {
            if (CurrentUserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var progress = await query.GetProgressAsync(userId, assetId, cancellationToken);
            return progress is null ? Results.NoContent() : Results.Ok(progress);
        });

        // Batch form of the route above: the per-episode UI needs the watched flag of a whole season in
        // one call. Repeated query parameter: ?assetIds=<guid>&assetIds=<guid>.
        group.MapGet("/progress", async (
            Guid[]? assetIds,
            ClaimsPrincipal principal,
            IPlaybackQuery query,
            CancellationToken cancellationToken) =>
        {
            if (CurrentUserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var requested = assetIds ?? [];
            if (requested.Length > MaxProgressAssetsPerRequest)
            {
                return Results.UnprocessableEntity(
                    new { error = $"At most {MaxProgressAssetsPerRequest} assets may be requested at once." });
            }

            var progress = await query.GetProgressForAssetsAsync(userId, requested, cancellationToken);
            return Results.Ok(progress);
        });

        // "Continue watching": the caller's started, unfinished items, most recent first.
        group.MapGet("/in-progress", async (
            int? limit,
            ClaimsPrincipal principal,
            IPlaybackQuery query,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var items = await query.GetInProgressAsync(viewer, limit ?? DefaultInProgress, cancellationToken);
            return Results.Ok(items);
        });

        group.MapGet("/next-up/{workId:guid}", async (
            Guid workId,
            ClaimsPrincipal principal,
            IPlaybackQuery query,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var nextUp = await query.GetNextUpAsync(viewer, workId, cancellationToken);
            return nextUp is null ? Results.NoContent() : Results.Ok(nextUp);
        });

        group.MapGet("/sessions/{id:guid}/stream", async (
            Guid id,
            ClaimsPrincipal principal,
            PlaybackStreamer streamer,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var file = await streamer.ResolveDirectFileAsync(viewer, new PlaybackSessionId(id), cancellationToken);
            return file is null
                ? Results.NotFound()
                : Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        });

        // A subtitle track as WebVTT. Fetched by the web client with its Authorization header, never with
        // a token in the URL: unlike the stream, nothing forces this one through a media element.
        group.MapGet("/sessions/{id:guid}/subtitles/{index:int}", async (
            Guid id,
            int index,
            HttpContext http,
            ClaimsPrincipal principal,
            PlaybackSubtitles subtitles,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var vtt = await subtitles.GetWebVttAsync(viewer, new PlaybackSessionId(id), index, cancellationToken);
            if (vtt is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Text(vtt, "text/vtt; charset=utf-8");
        });

        group.MapGet("/sessions/{id:guid}/hls/{file}", async (
            Guid id,
            string file,
            HttpContext http,
            ClaimsPrincipal principal,
            PlaybackStreamer streamer,
            CancellationToken cancellationToken) =>
        {
            if (Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            var resolved = await streamer.ResolveHlsFileAsync(viewer, new PlaybackSessionId(id), file, cancellationToken);
            if (resolved is null)
            {
                return Results.NotFound();
            }

            if (QueryTokenFor(http.Request, file) is { } token)
            {
                // Native HLS: the segments must carry the token the playlist was fetched with. The
                // answer now holds a credential, so nothing between here and the player may keep it.
                var playlist = await File.ReadAllTextAsync(resolved.Path, cancellationToken);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Text(HlsPlaylist.CarryQueryToken(playlist, token), resolved.ContentType);
            }

            return Results.File(resolved.Path, resolved.ContentType, enableRangeProcessing: true);
        });

        return endpoints;
    }

    /// <summary>
    /// The query token a playlist request authenticated with, or null. With an <c>Authorization</c>
    /// header the header is what authenticated (it wins over the query), so a query value alongside it
    /// proves nothing and is never copied into the answer.
    /// </summary>
    private static string? QueryTokenFor(HttpRequest request, string file)
    {
        if (!file.EndsWith(".m3u8", StringComparison.Ordinal) || request.Headers.Authorization.Count > 0)
        {
            return null;
        }

        var token = request.Query["access_token"].ToString();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static Guid? CurrentUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static object ToTicketDto(PlaybackTicket ticket) => new
    {
        sessionId = ticket.SessionId.ToString(),
        method = ticket.Method.ToString(),
        streamUrl = StreamUrl(ticket),
        resumePositionTicks = ticket.ResumePositionTicks,
        selection = new { audio = ticket.Selection.AudioStreamIndex, subtitle = ticket.Selection.SubtitleStreamIndex },
        plan = ToPlanDto(ticket.Plan),
        media = ticket.Media is { } media ? ToMediaDto(ticket.SessionId, media) : null,
    };

    private static object ToMediaDto(PlaybackSessionId sessionId, PlaybackMediaView media) => new
    {
        audioTracks = media.AudioTracks.Select(t => new
        {
            index = t.StreamIndex,
            language = t.Language,
            codec = t.Codec,
            channels = t.Channels,
            isDefault = t.IsDefault,
        }),
        subtitleTracks = media.SubtitleTracks.Select(t => new
        {
            index = t.StreamIndex,
            language = t.Language,
            codec = t.Codec,
            isForced = t.IsForced,
            isDefault = t.IsDefault,
            isExternal = t.IsExternal,
            // Only a track the server can hand over as text gets a URL; a picture track is listed, unplayable.
            url = t.CanDisplay
                ? $"/api/playback/sessions/{sessionId}/subtitles/{t.StreamIndex.ToString(CultureInfo.InvariantCulture)}"
                : null,
            canBurnIn = t.CanBurnIn,
        }),
        qualities = media.Qualities.Select(q => new
        {
            id = q.Id,
            maxWidth = q.MaxWidth,
            maxHeight = q.MaxHeight,
            maxBitrateKbps = q.MaxBitrateKbps,
        }),
        quality = media.Quality,
        streamOffsetTicks = media.StreamOffsetTicks,
        durationTicks = media.DurationTicks,
        burnedInSubtitle = media.BurnedInSubtitle,
    };

    private static string StreamUrl(PlaybackTicket ticket) => ticket.Method == PlaybackMethod.DirectPlay
        ? $"/api/playback/sessions/{ticket.SessionId}/stream"
        : $"/api/playback/sessions/{ticket.SessionId}/hls/manifest.m3u8";

    private static object ToDetailDto(PlaybackSessionDetail detail) => new
    {
        session = new
        {
            id = detail.Session.Id.ToString(),
            userId = detail.Session.UserId.ToString(),
            assetId = detail.Session.AssetId.ToString(),
            state = detail.Session.State.ToString(),
            method = detail.Session.Method.ToString(),
            positionTicks = detail.Session.PositionTicks,
            endReason = detail.Session.EndReason?.ToString(),
        },
        selection = new { audio = detail.Selection.AudioStreamIndex, subtitle = detail.Selection.SubtitleStreamIndex },
        plan = ToPlanDto(detail.Plan),
    };

    private static object ToPlanDto(PlaybackPlanView plan) => new
    {
        method = plan.Method.ToString(),
        transcodeReasons = plan.TranscodeReasons,
        decisions = plan.Decisions.Select(d => new
        {
            property = d.Property,
            expected = d.Expected,
            actual = d.Actual,
            verdict = d.Verdict,
        }),
        backend = plan.Backend.ToString(),
        accelerationReasons = plan.AccelerationReasons,
        decodeAccelerated = plan.DecodeAccelerated,
        targetMaxWidth = plan.TargetMaxWidth,
        targetMaxHeight = plan.TargetMaxHeight,
        targetBitrateKbps = plan.TargetBitrateKbps,
        outputCodec = plan.OutputCodec?.ToString(),
        toneMapped = plan.ToneMapped,
        burnInSubtitleIndex = plan.BurnInSubtitleIndex,
        audioChannels = plan.AudioChannels,
    };
}

/// <summary>
/// Body of a playback request: the asset, what the client can play, and — optionally — what the viewer
/// chose (track, quality, where to start).
/// </summary>
public sealed record RequestPlaybackBody(Guid AssetId, ClientCapability Capability, PlaybackPreferences? Preferences = null);

/// <summary>Body of a subtitle switch: the subtitle stream now shown, or null for none.</summary>
public sealed record SubtitleChoiceBody(int? SubtitleStreamIndex);

/// <summary>Body of a progress report: the client's position, the total runtime, and pause state.</summary>
public sealed record ProgressBody(long PositionTicks, long DurationTicks, bool IsPaused);
