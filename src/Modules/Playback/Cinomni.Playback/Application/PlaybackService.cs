using System.Diagnostics;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Diagnostics;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Application;

/// <summary>
/// Drives the playback lifecycle. Requesting playback queries Library for the asset (never touching
/// its tables), asks the planner to decide the delivery method (explainable), starts an FFmpeg HLS
/// transcode out-of-process when needed, and persists the session with its plan and its
/// <c>PlaybackStarted</c> event in one unit of work. Progress reports advance the session and
/// upsert the per-(user,asset) resume position.
/// <para>
/// A transcode needs a slot on the node first (<see cref="ActiveTranscodes"/>); a request past the
/// node's or the account's limit is refused with the reason, and nothing is started. From the moment a
/// slot is admitted every way out of the request either hands the process to the registry or stops it,
/// so no FFmpeg is ever left running that nothing can stop.
/// </para>
/// </summary>
public sealed class PlaybackService(
    PlaybackDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ILibraryQuery library,
    IContentAccess access,
    IPlaybackPlanner planner,
    IMediaEncoder encoder,
    ActiveTranscodes transcodes,
    SessionCloser closer,
    PlaybackOptions options,
    IVideoRangeProbe videoRange,
    HardwareCapabilitiesCache hardware,
    ILiveOptions<TranscodingOptions> transcoding,
    ILogger<PlaybackService> logger) : IPlaybackSessionCommands
{
    public async Task<Result<PlaybackTicket>> RequestPlaybackAsync(
        Viewer viewer,
        Guid assetId,
        ClientCapability capability,
        PlaybackPreferences? preferences = null,
        CancellationToken cancellationToken = default)
    {
        var userId = viewer.UserId;

        var asset = await library.GetAsync(new MediaAssetId(assetId), cancellationToken);

        // A work this account may not see reads exactly like an asset that does not exist: same error,
        // same shape, no oracle. Access is checked here and again on every file the streamer serves.
        // Only an active asset plays. One that was upgraded away names a version that is history — its
        // replacement usually lands on the very same path, so planning from the old streams would serve
        // the new file under the old file's description.
        if (asset is null
            || asset.Asset.State != MediaAssetState.Active
            || asset.Versions.Count == 0
            || !await access.CanSeeWorkAsync(viewer, asset.Asset.WorkId, cancellationToken))
        {
            return Result<PlaybackTicket>.Failure(new Error(PlaybackErrors.AssetNotFound, "No playable asset was found."));
        }

        var version = PrimaryVersion(asset);
        var progress = await dbContext.Progress
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.AssetId == assetId, cancellationToken);

        var quality = QualityLadder.Find(preferences?.Quality, out var knownQuality);
        if (!knownQuality
            || !PlaybackTracks.TrySelectAudio(version, preferences?.AudioStreamIndex, progress?.AudioStreamIndex, out var audioIndex)
            || !PlaybackTracks.TrySelectSubtitle(version, preferences, progress?.SubtitleStreamIndex, out var subtitleIndex))
        {
            return Result<PlaybackTicket>.Failure(
                new Error(PlaybackErrors.InvalidPreference, "That track or quality is not available for this title."));
        }

        // A picture subtitle is burned in only when the viewer asked for it by name: one the file flags as
        // its default must not turn every play of that file into a conversion.
        var imageSubtitle = preferences?.SubtitleStreamIndex is { } chosen
            && version.Streams.FirstOrDefault(s => s.StreamIndex == chosen) is { Type: MediaStreamType.Subtitle } chosenStream
            && !PlaybackTracks.CanDisplay(chosenStream)
                ? chosen
                : (int?)null;
        var source = await BuildSourceAsync(version, audioIndex, imageSubtitle, cancellationToken);
        var plan = planner.Plan(source, capability, quality);
        var resumePosition = preferences?.StartPositionTicks is { } start
            ? Math.Max(0, start)
            : progress is { Played: false } ? progress.PositionTicks : 0;
        var durationTicks = DurationTicksOf(version, progress);

        // Only a conversion starts part way into the file. A file played as-is is seeked by the player,
        // and a repackage copies from the keyframe before the position, which would put the stream's
        // zero somewhere other than where the player is told it is.
        var streamOffset = plan.Method == PlaybackMethod.Transcode && !NearTheEnd(resumePosition, durationTicks)
            ? resumePosition
            : 0;
        var now = DateTimeOffset.UtcNow;

        var session = PlaybackSession.Create(
            userId, assetId, asset.Asset.WorkId, version.Id.Value, version.FullPath, ContainerOf(version.FullPath),
            plan, audioIndex, subtitleIndex, resumePosition, playSessionId: null, now);
        session.Begin(now);

        var events = new List<IDomainEvent> { new PlaybackStarted(session.Id, userId, assetId, plan.Method.ToString()) };
        var video = VideoOf(version);
        var canBurnIn = transcoding.Current.BurnInImageSubtitles && hardware.Current.SubtitleOverlay;
        var media = new PlaybackMediaView(
            PlaybackTracks.Audio(version),
            PlaybackTracks.Subtitles(version, canBurnIn),
            QualityLadder.OfferedFor(video?.Width, video?.Height),
            quality?.Id ?? QualityLadder.Original,
            streamOffset,
            durationTicks,
            plan.BurnInSubtitleIndex);
        var ticket = new PlaybackTicket(
            new PlaybackSessionId(session.Id), plan.Method, plan.ToView(),
            new StreamSelectionView(audioIndex, subtitleIndex), resumePosition, media);

        if (!PlaybackMetrics.IsEncoded(plan.Method))
        {
            await PersistAsync(session, events, cancellationToken);
            return Result<PlaybackTicket>.Success(ticket);
        }

        var admission = transcodes.TryAdmit(session.Id, userId);
        if (admission != TranscodeAdmission.Admitted)
        {
            PlaybackMetrics.RecordRefused(plan.Method.ToString());
            logger.LogInformation("Refused a {Method} for session {SessionId}: {Admission}.", plan.Method, session.Id, admission);
            return Result<PlaybackTicket>.Failure(Refusal(admission));
        }

        try
        {
            var failure = await StartTranscodeAsync(
                session, plan, source, audioIndex, subtitleIndex, streamOffset, events, now, cancellationToken);
            if (failure is not null)
            {
                // The slot and any partial output go now; the failed session row is history worth keeping.
                await transcodes.EndAsync(session.Id);
            }

            await PersistAsync(session, events, cancellationToken);
            return failure is { } error ? Result<PlaybackTicket>.Failure(error) : Result<PlaybackTicket>.Success(ticket);
        }
        catch
        {
            // A cancelled request or a row that never committed: nothing else will ever name this
            // process, so it is stopped here or never.
            await transcodes.EndAsync(session.Id);
            throw;
        }
    }

    public async Task ReportProgressAsync(
        Viewer viewer,
        PlaybackSessionId sessionId,
        long positionTicks,
        long durationTicks,
        bool isPaused,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId.Value, cancellationToken);
        if (session is null || session.UserId != viewer.UserId)
        {
            return; // unknown session or not the caller's
        }

        // Access again, on every report, like every segment the streamer serves. A viewer who may no
        // longer see the title does not get to keep its stream alive — and its slot — by reporting: the
        // session is closed now instead of when it goes idle.
        if (session.WorkId is not { } workId || !await access.CanSeeWorkAsync(viewer, workId, cancellationToken))
        {
            await closer.CloseAsync(session.Id, PlaybackEndReason.AccessRevoked, cancellationToken);
            return;
        }

        // A report is the viewer saying they are still there, whatever it says about the position.
        transcodes.Touch(session.Id);

        var now = DateTimeOffset.UtcNow;
        var completed = session.ReportProgress(positionTicks, durationTicks, isPaused, now);
        if (session.State == PlaybackState.Starting)
        {
            return; // a report before Begin — nothing to persist
        }

        // Watched past the threshold completes the session, but the stream stays on the gauge until it
        // really ends: the credits are still being converted and served.
        await UpsertProgressAsync(session, positionTicks, durationTicks, completed, now, cancellationToken);

        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(
                new PlaybackProgressUpdated(session.Id, session.PositionTicks, isPaused, session.ProgressSeq), token);
            if (completed)
            {
                await eventBus.PublishAsync(new PlaybackCompleted(session.Id, session.UserId, session.AssetId), token);
            }
        }, cancellationToken);
    }

    public async Task<bool> ChooseSubtitleAsync(
        Viewer viewer,
        PlaybackSessionId sessionId,
        int? subtitleStreamIndex,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId.Value, cancellationToken);
        if (session is null || session.UserId != viewer.UserId)
        {
            return false;
        }

        if (subtitleStreamIndex is { } index)
        {
            var asset = await library.GetAsync(new MediaAssetId(session.AssetId), cancellationToken);
            var version = asset?.Versions.FirstOrDefault(v => v.Id.Value == session.VersionId);
            if (version?.Streams.Any(s => s.StreamIndex == index && s.Type == MediaStreamType.Subtitle) != true)
            {
                return false;
            }
        }

        session.ChooseSubtitle(subtitleStreamIndex, DateTimeOffset.UtcNow);
        await unitOfWork.ExecuteAsync(token => dbContext.SaveChangesAsync(token), cancellationToken);
        return true;
    }

    public async Task StopPlaybackAsync(Guid userId, PlaybackSessionId sessionId, CancellationToken cancellationToken = default)
    {
        var owner = await dbContext.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId.Value)
            .Select(s => (Guid?)s.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (owner != userId)
        {
            return; // unknown session or not the caller's: nothing is stopped
        }

        await closer.CloseAsync(sessionId.Value, PlaybackEndReason.Stopped, cancellationToken);
    }

    private static Error Refusal(TranscodeAdmission admission) => admission == TranscodeAdmission.AccountFull
        ? new Error(
            PlaybackErrors.TranscodeLimit,
            "You already have as many streams being converted as your account allows. Stop one and try again.")
        : new Error(
            PlaybackErrors.TranscodeLimit,
            "The server is converting as many streams as it can right now. Try again when one of them ends.");

    private async Task<Error?> StartTranscodeAsync(
        PlaybackSession session,
        PlaybackPlan plan,
        PlaybackSource source,
        int? audioIndex,
        int? subtitleIndex,
        long streamOffsetTicks,
        List<IDomainEvent> events,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var copy = plan.Method == PlaybackMethod.Remux;
        var hevcOut = !copy && plan.OutputCodec == VideoOutputCodec.Hevc;
        var videoCodec = copy ? "copy" : hevcOut ? "libx265" : "libx264";
        var audioCodec = copy ? "copy" : "aac";
        var job = TranscodeJob.Prepare(session.Id, videoCodec, audioCodec, hls: true, now);
        session.AddTranscodeJob(job);
        events.Add(new TranscodeStarted(job.Id, session.Id, plan.TranscodeReasons));

        var outputDirectory = Path.Combine(options.TranscodeRoot, session.Id.ToString());
        var method = plan.Method.ToString();

        // One span for the launch, so an FFmpeg that takes ten seconds to produce a manifest is visible
        // as ten seconds of somebody waiting. No path and no account: the tag is the bounded method.
        using var activity = CinomniTelemetry.Source.StartActivity("transcode.start", ActivityKind.Internal);
        activity?.SetTag(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Playback);
        activity?.SetTag(CinomniTelemetry.Tags.Method, method);

        try
        {
            // Out-of-process side effect, outside the transaction.
            var output = await encoder.StartHlsAsync(
                new TranscodeRequest(
                    session.FullPath, outputDirectory, videoCodec, audioCodec, plan.Backend, plan.DecodeAccelerated,
                    audioIndex, subtitleIndex, plan.TargetMaxWidth, plan.TargetMaxHeight, plan.TargetBitrateKbps,
                    TimeSpan.FromTicks(streamOffsetTicks).TotalSeconds)
                {
                    ToneMap = plan.ToneMapped,
                    BurnInSubtitleIndex = plan.BurnInSubtitleIndex,
                    AudioChannels = plan.AudioChannels,
                    // A browser plays HEVC only from fragmented MP4 — a conversion to it, or a copy of it.
                    Fmp4Segments = hevcOut || (copy && string.Equals(source.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)),
                    Tuning = EncodeTuning.From(transcoding.Current),
                },
                cancellationToken);
            if (!transcodes.Attach(session.Id, output.Process))
            {
                // The slot was ended while FFmpeg was starting (the host is stopping, or the start took
                // longer than the idle timeout): this process has no owner but us.
                await output.Process.DisposeAsync();
                throw new InvalidOperationException("The transcode was released before it could be served.");
            }

            job.MarkRunning(
                Path.GetDirectoryName(output.ManifestPath) ?? outputDirectory,
                output.Process.ProcessId,
                output.Process.StartedAt);
            if (output.BackendUsed != plan.Backend)
            {
                // The encoder already retried once, bounded, inside StartHlsAsync — this only persists
                // the fact so a session transcoding in software despite a plan naming hardware is
                // explainable rather than a silent discrepancy.
                job.RecordFallback($"hardware backend '{plan.Backend}' failed; fell back to '{output.BackendUsed}'");
                logger.LogWarning(
                    "Session {SessionId} fell back from {Planned} to {Used} after the planned backend failed.",
                    session.Id, plan.Backend, output.BackendUsed);
            }

            PlaybackMetrics.RecordStarted(session.Id, method);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // What FFmpeg wrote to stderr can quote the media path, so it goes on the job row and nowhere
            // else: the log, the trace and the event carry the exit code. Any other failure is ours, and
            // its stack is what somebody debugging it needs.
            if (ex is TranscodeStartFailedException ffmpeg)
            {
                logger.LogWarning(
                    "Transcode failed to start for session {SessionId}: {Summary}", session.Id, ffmpeg.Message);
                job.MarkFailed(TranscodeJob.Explain(ffmpeg.Message, ffmpeg.Diagnostics));
            }
            else
            {
                logger.LogWarning(ex, "Transcode failed to start for session {SessionId}.", session.Id);
                job.MarkFailed(ex.Message);
            }

            activity?.SetStatus(ActivityStatusCode.Error);
            PlaybackMetrics.RecordFailed(method);
            session.Fail(now);
            events.Add(new TranscodeFailed(job.Id, ex.Message));
            return new Error(PlaybackErrors.TranscodeFailed, "Could not start the transcode.");
        }
    }

    private async Task PersistAsync(PlaybackSession session, IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Sessions.Add(session); // new root → cascade inserts the transcode job
            await dbContext.SaveChangesAsync(token);
            foreach (var domainEvent in events)
            {
                await eventBus.PublishAsync(domainEvent, token);
            }
        }, cancellationToken);

    private async Task UpsertProgressAsync(
        PlaybackSession session,
        long positionTicks,
        long durationTicks,
        bool completed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var progress = await dbContext.Progress.FirstOrDefaultAsync(
            p => p.UserId == session.UserId && p.AssetId == session.AssetId, cancellationToken);
        if (progress is null)
        {
            progress = PlaybackProgress.Create(session.UserId, session.AssetId, now);
            dbContext.Progress.Add(progress);
        }

        await CorrelateAsync(progress, session.AssetId, cancellationToken);

        progress.Record(positionTicks, durationTicks, session.AudioStreamIndex, session.SubtitleStreamIndex, now);
        if (completed)
        {
            progress.CountPlay(now);
        }
    }

    /// <summary>
    /// Stamps the row's catalog correlation from the asset (⇢ Library, by interface). Only the first
    /// report of a (user, asset) pair pays for the lookup — and it also back-fills rows written before
    /// the columns existed, so "continue watching" converges without a data migration.
    /// </summary>
    private async Task CorrelateAsync(PlaybackProgress progress, Guid assetId, CancellationToken cancellationToken)
    {
        if (progress is { WorkId: not null, UnitId: not null })
        {
            return;
        }

        var asset = await library.GetAsync(new MediaAssetId(assetId), cancellationToken);
        if (asset is null)
        {
            return;
        }

        // A multi-episode file serves several units and progress is per asset, so the first unit
        // is the one recorded — see PlaybackProgress.UnitId.
        progress.Correlate(asset.Asset.WorkId, asset.UnitIds?.FirstOrDefault());
    }

    /// <summary>
    /// The file's runtime: what the probe measured, else what a player last reported for it — a version
    /// registered before the runtime was recorded still gets a full timeline once it has been played.
    /// </summary>
    private static long? DurationTicksOf(MediaVersionSummary version, PlaybackProgress? progress)
    {
        if (version.DurationSeconds is > 0 and var seconds)
        {
            return TimeSpan.FromSeconds(seconds).Ticks;
        }

        return progress is { DurationTicks: > 0 } ? progress.DurationTicks : null;
    }

    /// <summary>A start this close to the end would convert nothing worth watching; the stream starts at zero instead.</summary>
    private static bool NearTheEnd(long positionTicks, long? durationTicks) =>
        durationTicks is { } duration && positionTicks >= duration - TimeSpan.FromSeconds(30).Ticks;

    private static MediaVersionSummary PrimaryVersion(MediaAssetDetail asset) =>
        asset.Versions.FirstOrDefault(v => v.Id == asset.Asset.PrimaryVersionId) ?? asset.Versions[0];

    private static MediaStreamSummary? VideoOf(MediaVersionSummary version) =>
        version.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

    /// <summary>
    /// The planner's view of the file, with the audio that will actually play: only the selected track's
    /// codec has to be one the client decodes — another track it cannot play is never sent to it.
    /// </summary>
    private async Task<PlaybackSource> BuildSourceAsync(
        MediaVersionSummary version, int? audioIndex, int? imageSubtitle, CancellationToken cancellationToken)
    {
        var video = VideoOf(version);
        var selected = version.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.StreamIndex == audioIndex);
        IReadOnlyList<string> audioCodecs = selected?.Codec is { } codec ? [codec] : [];

        // Library knows the range of anything Import read since it started reading it; for the rest the
        // file's own header is asked once, and the answer kept.
        var range = video?.VideoRangeType;
        if (video is not null && range is null)
        {
            range = await videoRange.ProbeAsync(version.Id.Value, version.FullPath, cancellationToken);
        }

        return new PlaybackSource(
            ContainerOf(version.FullPath), video?.Codec, video?.Height, audioCodecs, video?.Width, version.Bitrate,
            SelectedAudioIsDefault: audioIndex == PlaybackTracks.DefaultAudio(version),
            VideoRange: range,
            AudioChannels: selected?.Channels,
            ImageSubtitleIndex: imageSubtitle);
    }

    /// <summary>Maps a file extension to the ffprobe-style container name the client capabilities use.</summary>
    private static string ContainerOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mkv" => "matroska",
        ".mp4" or ".m4v" => "mp4",
        ".webm" => "webm",
        ".mov" => "mov",
        ".avi" => "avi",
        ".ts" => "mpegts",
        var other => other.TrimStart('.'),
    };
}
