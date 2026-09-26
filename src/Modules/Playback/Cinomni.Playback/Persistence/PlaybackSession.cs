using System.ComponentModel.DataAnnotations.Schema;
using Cinomni.Kernel.Identifiers;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Planning;

namespace Cinomni.Playback.Persistence;

/// <summary>
/// The Playback aggregate root (persisted): a session that delivers an asset to a user in
/// one of three modes decided by the planner. Its finite-state machine is pure, guarded logic. The
/// explainable <see cref="PlaybackPlan"/> travels as a <c>jsonb</c> document so every decision is
/// auditable. Server-side progress advances the position and completes the session past the watched
/// threshold even while the client is quiet.
/// </summary>
public sealed class PlaybackSession
{
    private const int PathMaxLength = 2048;
    private const int ContainerMaxLength = 80;
    private const int TokenMaxLength = 100;

    /// <summary>The fraction of the runtime past which the session counts as watched (resume threshold).</summary>
    private const double WatchedThreshold = 0.90;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public Guid AssetId { get; init; }

    /// <summary>
    /// The catalog work this asset belongs to, snapshotted when the session opened. Access is re-checked
    /// against it on every segment, so revoking a collection stops an in-flight stream. Null on a session
    /// that predates the column, which serves nothing (fail closed).
    /// </summary>
    public Guid? WorkId { get; init; }

    public Guid VersionId { get; init; }

    /// <summary>The file to serve for Direct Play / feed to FFmpeg (from Library, already validated).</summary>
    public required string FullPath { get; init; }

    public string? Container { get; init; }

    public PlaybackState State { get; private set; }

    /// <summary>Why the session ended; null while it is open, and on sessions that ended before it was recorded.</summary>
    public PlaybackEndReason? EndReason { get; private set; }

    public PlaybackMethod Method { get; init; }

    /// <summary>Opaque client-supplied session token (correlates the client's stream requests).</summary>
    public string? PlaySessionId { get; init; }

    /// <summary>Serialized <see cref="PlaybackPlan"/> (jsonb). Read through <see cref="Plan"/>.</summary>
    public required string PlanJson { get; init; }

    public int? AudioStreamIndex { get; init; }

    /// <summary>
    /// The subtitle track shown, or null for none. Unlike the audio, the viewer switches it in the player
    /// without a new session (see <see cref="ChooseSubtitle"/>), and progress records what it is now.
    /// </summary>
    public int? SubtitleStreamIndex { get; private set; }

    public long PositionTicks { get; private set; }

    public long DurationTicks { get; private set; }

    /// <summary>Monotonic progress counter, so each <c>PlaybackProgressUpdated</c> is distinct.</summary>
    public int ProgressSeq { get; private set; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic-concurrency counter.</summary>
    public int Version { get; private set; }

    public List<TranscodeJob> TranscodeJobs { get; } = [];

    /// <summary>The explainable plan, projected from <see cref="PlanJson"/>.</summary>
    [NotMapped]
    public PlaybackPlan Plan => PlaybackPlan.FromJson(PlanJson);

    private bool IsTerminal => State is PlaybackState.Completed or PlaybackState.Failed;

    /// <summary>The active delivery state implied by the chosen method (where Resume lands after a Pause).</summary>
    private PlaybackState ActiveState => Method switch
    {
        PlaybackMethod.DirectPlay => PlaybackState.DirectPlaying,
        PlaybackMethod.Remux => PlaybackState.Remuxing,
        _ => PlaybackState.Transcoding,
    };

    /// <summary>Opens a session in <see cref="PlaybackState.Starting"/> at the resume position.</summary>
    public static PlaybackSession Create(
        Guid userId,
        Guid assetId,
        Guid workId,
        Guid versionId,
        string fullPath,
        string? container,
        PlaybackPlan plan,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        long resumePositionTicks,
        string? playSessionId,
        DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        UserId = userId,
        AssetId = assetId,
        WorkId = workId,
        VersionId = versionId,
        FullPath = Text.Truncate(fullPath, PathMaxLength)!,
        Container = Text.Truncate(container, ContainerMaxLength),
        State = PlaybackState.Starting,
        Method = plan.Method,
        PlaySessionId = Text.Truncate(playSessionId, TokenMaxLength),
        PlanJson = plan.ToJson(),
        AudioStreamIndex = audioStreamIndex,
        SubtitleStreamIndex = subtitleStreamIndex,
        PositionTicks = resumePositionTicks < 0 ? 0 : resumePositionTicks,
        StartedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Starting → the delivery state chosen by the plan. Emits <c>PlaybackStarted</c> (caller).</summary>
    public void Begin(DateTimeOffset now)
    {
        if (State != PlaybackState.Starting)
        {
            throw new InvalidOperationException($"Cannot begin session {Id}: it is {State}, expected Starting.");
        }

        Transition(ActiveState, now);
    }

    /// <summary>
    /// Applies a client progress report: advances the position, flips play/pause, and completes the
    /// session once the watched threshold is crossed. Returns true exactly when it just completed (so
    /// the caller emits <c>PlaybackCompleted</c> once). A report on a terminal session is a no-op.
    /// </summary>
    public bool ReportProgress(long positionTicks, long durationTicks, bool isPaused, DateTimeOffset now)
    {
        if (IsTerminal || State == PlaybackState.Starting)
        {
            return false;
        }

        PositionTicks = positionTicks < 0 ? 0 : positionTicks;
        if (durationTicks > 0)
        {
            DurationTicks = durationTicks;
        }

        ProgressSeq += 1;
        UpdatedAt = now;
        Version += 1;

        if (DurationTicks > 0 && (double)PositionTicks / DurationTicks >= WatchedThreshold)
        {
            End(PlaybackState.Completed, PlaybackEndReason.Watched, now);
            return true;
        }

        if (isPaused && State != PlaybackState.Paused)
        {
            Transition(PlaybackState.Paused, now);
        }
        else if (!isPaused && State == PlaybackState.Paused)
        {
            Transition(ActiveState, now);
        }

        return false;
    }

    /// <summary>
    /// Records the subtitle track the viewer switched to (null: off), so the title remembers it. The
    /// caller has checked the index names a subtitle stream of the file. A no-op once the session ended.
    /// </summary>
    public void ChooseSubtitle(int? subtitleStreamIndex, DateTimeOffset now)
    {
        if (IsTerminal || SubtitleStreamIndex == subtitleStreamIndex)
        {
            return;
        }

        SubtitleStreamIndex = subtitleStreamIndex;
        UpdatedAt = now;
        Version += 1;
    }

    /// <summary>
    /// Ends the session as over rather than broken: the viewer stopped, went silent, lost access, or the
    /// stream reached its lifetime. Idempotent for an already-terminal session, which keeps its reason.
    /// </summary>
    public bool Stop(DateTimeOffset now, PlaybackEndReason reason = PlaybackEndReason.Stopped)
    {
        if (IsTerminal)
        {
            return false;
        }

        End(PlaybackState.Completed, reason, now);
        return true;
    }

    /// <summary>Fails the session (a fatal transcode error). A session that already ended keeps its reason.</summary>
    public void Fail(DateTimeOffset now, PlaybackEndReason reason = PlaybackEndReason.TranscodeFailed)
    {
        if (IsTerminal)
        {
            return;
        }

        End(PlaybackState.Failed, reason, now);
    }

    public void AddTranscodeJob(TranscodeJob job) => TranscodeJobs.Add(job);

    /*
     * Every change to a transcode job goes through the session and bumps its Version. The job table
     * has no concurrency token of its own, so this is what makes two writers racing on one stream —
     * a stop and a recorded FFmpeg exit, say — collide instead of the second silently undoing the
     * first. All of these need TranscodeJobs loaded.
     */

    /// <summary>
    /// Records that this session's converted output is gone (its process stopped, its directory
    /// reclaimed). Idempotent.
    /// </summary>
    /// <returns><c>true</c> when any job changed.</returns>
    public bool ReleaseTranscode(DateTimeOffset now)
    {
        var changed = false;
        foreach (var job in TranscodeJobs)
        {
            changed |= job.MarkCleaned();
        }

        if (changed)
        {
            Touch(now);
        }

        return changed;
    }

    /// <summary>
    /// Records how FFmpeg ended on its own. Having written the whole file changes only the job. A
    /// failure part way through also fails an open session: its playlist will never be finished, and a
    /// player left polling it would buffer for ever.
    /// </summary>
    /// <returns>The jobs that failed just now (for their <c>TranscodeFailed</c>), or <c>null</c> when
    /// nothing changed.</returns>
    public IReadOnlyList<TranscodeJob>? RecordTranscodeExit(int exitCode, string diagnostics, DateTimeOffset now)
    {
        var changed = TranscodeJobs.Where(job => job.RecordExit(exitCode, diagnostics)).ToList();
        if (changed.Count == 0)
        {
            return null;
        }

        var failed = changed.Where(job => job.State == TranscodeState.Failed).ToList();
        if (failed.Count > 0 && !IsTerminal)
        {
            End(PlaybackState.Failed, PlaybackEndReason.TranscodeFailed, now);
        }
        else
        {
            Touch(now);
        }

        return failed;
    }

    /// <summary>
    /// Fails a session whose transcode died with the process that ran it — a restart, not anything the
    /// viewer or the file did — and records why on each job that was still producing output. A session
    /// that already ended is left as it is.
    /// </summary>
    /// <returns>The jobs failed just now, or <c>null</c> when the session had already ended.</returns>
    public IReadOnlyList<TranscodeJob>? Interrupt(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return null;
        }

        var failed = TranscodeJobs
            .Where(job => job.State is not (TranscodeState.Failed or TranscodeState.Cleaned))
            .ToList();
        foreach (var job in failed)
        {
            job.MarkFailed(reason);
        }

        End(PlaybackState.Failed, PlaybackEndReason.ServerRestarted, now);
        return failed;
    }

    /// <summary>Whether the session has ended, completed or failed.</summary>
    public bool HasEnded => IsTerminal;

    /// <summary>Whether the position is far enough in to be worth resuming (past the 5% floor).</summary>
    public bool HasMeaningfulProgress => DurationTicks > 0 && (double)PositionTicks / DurationTicks >= 0.05;

    public bool IsWatched => DurationTicks > 0 && (double)PositionTicks / DurationTicks >= WatchedThreshold;

    private void End(PlaybackState to, PlaybackEndReason reason, DateTimeOffset now)
    {
        EndReason = reason;
        Transition(to, now);
    }

    private void Transition(PlaybackState to, DateTimeOffset now)
    {
        State = to;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version += 1;
    }
}
