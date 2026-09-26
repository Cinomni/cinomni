using System.ComponentModel.DataAnnotations.Schema;
using Cinomni.Downloads.Contracts;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Downloads.Persistence;

/// <summary>
/// The Downloads aggregate root: a <b>persisted</b> download task reconciled against the
/// libtorrent sidecar (cases #9/#13/#14). Unlike an in-memory tracker, it survives a restart and is
/// re-added from its resume-data checkpoint without a full recheck. The finite-state
/// machine is guarded, and every state change is appended to the history trail.
/// <para>
/// Two flavours of transition: <b>observed</b> ones fed by the sidecar status stream are monotone
/// and idempotent (they absorb the stream's noise — repeats and out-of-order states are no-ops), so
/// each real transition and its integration event fire exactly once; <b>control</b> ones (pause,
/// resume, remove) are strict and reject an illegal request.
/// </para>
/// </summary>
public sealed class DownloadTask
{
    private const int NameMaxLength = 500;
    private const int PathMaxLength = 2048;
    private const int ReasonMaxLength = 500;
    private const int UrlMaxLength = 2048;
    private const int InfoHashMaxLength = 64;

    public Guid Id { get; init; }

    /// <summary>The acquisition intent this download serves (correlation, echoed in our events).</summary>
    public Guid IntentId { get; init; }

    /// <summary>The acquisition attempt this download serves (correlation, echoed in our events).</summary>
    public Guid AttemptId { get; init; }

    /// <summary>The catalog work this download serves (opaque correlation, echoed forward to Import).</summary>
    public Guid WorkId { get; init; }

    /// <summary>The monitored target this download serves (opaque correlation, echoed forward to Import).</summary>
    public Guid TargetId { get; init; }

    public required string ReleaseGuid { get; init; }

    /// <summary>The indexer reference to fetch: a magnet URI or an http(s) link to a .torrent.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Staging area the sidecar writes into (Import lands files from here).</summary>
    public required string SavePath { get; init; }

    /// <summary>Assigned once the sidecar has the torrent (v1 info-hash, hex).</summary>
    public string? InfoHash { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DownloadState State { get; private set; }

    public double Progress { get; private set; }

    public long DownloadRate { get; private set; }

    public long UploadRate { get; private set; }

    public int NumPeers { get; private set; }

    public int NumSeeds { get; private set; }

    public long AllTimeUpload { get; private set; }

    public long AllTimeDownload { get; private set; }

    public int SeedingSeconds { get; private set; }

    /// <summary>Where the completed content landed in the staging area (for Import).</summary>
    public string? ContentPath { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The latest <c>.fastresume</c> blob — the checkpoint that survives a restart.</summary>
    public byte[]? ResumeData { get; private set; }

    public int CheckpointSeq { get; private set; }

    /// <summary>Serialized <see cref="SeedingPolicy"/> (jsonb). Read/written through <see cref="Policy"/>.</summary>
    public string SeedingPolicyJson { get; private set; } = SeedingPolicy.Unbounded.ToJson();

    /// <summary>When the transfer first started (guards a one-time <c>DownloadStarted</c>).</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>
    /// When this task was held because torrent egress could no longer be verified, or null when it is
    /// not held. Persisted rather than inferred: an installation that restarts mid-outage must come
    /// back knowing which downloads it stopped and why, or it would resume them into a leaking network.
    /// </summary>
    public DateTimeOffset? NetworkHoldSince { get; private set; }

    /// <summary>The observed reason for the hold, kept beside it so the pause is explainable.</summary>
    public string? NetworkHoldReason { get; private set; }

    /// <summary>
    /// When the transfer last moved, or last started trying to: the stall clock. Null until the first
    /// snapshot after it existed, which starts it — a task written before the clock existed is timed
    /// from the moment it is first observed, not from its creation.
    /// </summary>
    public DateTimeOffset? LastProgressAt { get; private set; }

    /// <summary>
    /// The furthest this transfer has got. The stall clock restarts only when progress passes it:
    /// libtorrent counts the blocks of a piece before the piece is verified, so a peer sending garbage
    /// moves progress up and the failed hash moves it back, and compared with the last snapshot that
    /// cycle would restart the clock for ever.
    /// </summary>
    public double BestProgress { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public List<TorrentFile> Files { get; } = [];

    public List<DownloadHistoryRecord> History { get; } = [];

    /// <summary>The catalog units this download serves (the work id for a movie, N episodes for a pack).</summary>
    public List<DownloadTaskUnit> Units { get; } = [];

    /// <summary>Every acquisition attempt waiting on this download — normally one, more on a shared pack.</summary>
    public List<DownloadTaskClaim> Claims { get; } = [];

    /// <summary>The seeding rule in force, projected from <see cref="SeedingPolicyJson"/>.</summary>
    [NotMapped]
    public SeedingPolicy Policy => SeedingPolicy.FromJson(SeedingPolicyJson);

    private bool IsTerminal => State is DownloadState.Completed or DownloadState.Seeding
        or DownloadState.Removed or DownloadState.Error;

    /// <summary>Creates a task in <see cref="DownloadState.Queued"/> from an acquisition hand-off.</summary>
    /// <param name="unitIds">
    /// The catalog units the download serves. Empty falls back to the work id, which is the movie
    /// unit and keeps the published correlation identical to what shipped before.
    /// </param>
    public static DownloadTask Create(
        Guid intentId,
        Guid attemptId,
        Guid workId,
        Guid targetId,
        string releaseGuid,
        string downloadUrl,
        string savePath,
        SeedingPolicy policy,
        DateTimeOffset now,
        IReadOnlyList<Guid>? unitIds = null)
    {
        var task = new DownloadTask
        {
            Id = Uuid7.New(),
            IntentId = intentId,
            AttemptId = attemptId,
            WorkId = workId,
            TargetId = targetId,
            ReleaseGuid = Text.Truncate(releaseGuid, UrlMaxLength)!,
            DownloadUrl = Text.Truncate(downloadUrl, UrlMaxLength)!,
            SavePath = Text.Truncate(savePath, PathMaxLength)!,
            State = DownloadState.Queued,
            SeedingPolicyJson = policy.ToJson(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        task.History.Add(new DownloadHistoryRecord
        {
            DownloadTaskId = task.Id,
            Seq = 0,
            FromState = DownloadState.Queued,
            ToState = DownloadState.Queued,
            Trigger = "AddDownload",
            OccurredAt = now,
            Note = null,
        });

        task.AddUnits(unitIds is { Count: > 0 } ? unitIds : [workId]);
        task.Claim(intentId, attemptId, targetId, isOriginating: true, now);
        return task;
    }

    /// <summary>Adds catalog units the task does not already serve. Returns the ones actually added.</summary>
    public IReadOnlyList<DownloadTaskUnit> AddUnits(IEnumerable<Guid> unitIds)
    {
        var known = Units.Select(u => u.UnitId).ToHashSet();
        var added = new List<DownloadTaskUnit>();
        foreach (var unitId in unitIds)
        {
            if (!known.Add(unitId))
            {
                continue;
            }

            var unit = new DownloadTaskUnit { DownloadTaskId = Id, UnitId = unitId };
            Units.Add(unit);
            added.Add(unit);
        }

        return added;
    }

    /// <summary>
    /// Records an acquisition attempt as waiting on this download. Returns null when that attempt is
    /// already recorded, which is what makes attaching a duplicate hand-off idempotent.
    /// </summary>
    public DownloadTaskClaim? Claim(Guid intentId, Guid attemptId, Guid targetId, bool isOriginating, DateTimeOffset now)
    {
        if (Claims.Any(c => c.AttemptId == attemptId))
        {
            return null;
        }

        var claim = new DownloadTaskClaim
        {
            DownloadTaskId = Id,
            AttemptId = attemptId,
            IntentId = intentId,
            TargetId = targetId,
            IsOriginating = isOriginating,
            CreatedAt = now,
        };
        Claims.Add(claim);
        return claim;
    }

    /// <summary>
    /// True while the sidecar is still working on this task. Only such a task may absorb another
    /// attempt's hand-off: a finished or removed one has already published its feedback, so attaching
    /// to it would leave the newcomer waiting for an event that will never come again.
    /// </summary>
    public bool IsInFlight =>
        State is DownloadState.Queued or DownloadState.ResolvingMetadata
            or DownloadState.Checking or DownloadState.Downloading or DownloadState.Paused;

    /// <summary>
    /// Records the engine's assigned info-hash and name. No state change.
    /// <para>
    /// Called on add, and again when the metadata resolves: until the file layout is known the name is
    /// provisional, because the only name a magnet has before then is its <c>dn=</c>, which need not
    /// be the folder the torrent writes to.
    /// </para>
    /// <para>
    /// The name is swarm-supplied and is reduced to a single safe path segment before it is kept,
    /// because this module composes the content path Import is told to scan from it. A name that
    /// sanitises away to nothing is not recorded at all: an empty name leaves the content path at the
    /// staging root, which is a directory of every download rather than of this one.
    /// </para>
    /// </summary>
    public void OnAdded(string infoHash, string name, DateTimeOffset now)
    {
        InfoHash = Text.Truncate(infoHash, InfoHashMaxLength);
        // Frozen once the content path is: Import was already told that folder, and a name that
        // moved after it would describe a download other than the one being imported.
        if (ContentPath is null && Text.SanitizeSegment(name) is { Length: > 0 } segment)
        {
            Name = Text.Truncate(segment, NameMaxLength)!;
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Records that this task was re-established with the engine after a restart, and returns the
    /// history line that says so. The state is deliberately <b>not</b> changed: whether the transfer
    /// is checking, downloading or already finished is the engine's answer, and it arrives on the
    /// very next status snapshot. What this records is the fact an operator needs afterwards — that
    /// the task was handed back to the engine, and whether it went back with its checkpoint or had
    /// to start from nothing.
    /// </summary>
    /// <param name="infoHash">The info-hash the engine assigned; normally the one already persisted.</param>
    /// <param name="name">The torrent name, which a magnet may only now have resolved.</param>
    /// <param name="resumedFromCheckpoint">Whether the engine accepted the persisted resume blob.</param>
    public DownloadHistoryRecord MarkRecovered(
        string infoHash,
        string name,
        bool resumedFromCheckpoint,
        DateTimeOffset now)
    {
        OnAdded(infoHash, name, now);

        var record = new DownloadHistoryRecord
        {
            DownloadTaskId = Id,
            Seq = History.Count == 0 ? 0 : History[^1].Seq + 1,
            FromState = State,
            ToState = State,
            Trigger = "Recover",
            OccurredAt = now,
            Note = resumedFromCheckpoint
                ? $"re-established from checkpoint {CheckpointSeq}"
                : "re-established without a usable checkpoint",
        };
        History.Add(record);
        RestartStallClock(now);
        UpdatedAt = now;
        return record;
    }

    // -- observed transitions (fed by the status stream; monotone + idempotent) ------------------

    /// <summary>Queued → ResolvingMetadata (a magnet fetching its metadata).</summary>
    public bool MarkResolvingMetadata(DateTimeOffset now) =>
        Advance(DownloadState.ResolvingMetadata, "ResolvingMetadata", now, DownloadState.Queued);

    /// <summary>Queued/ResolvingMetadata → Checking (hashing existing data on disk).</summary>
    public bool MarkChecking(DateTimeOffset now) =>
        Advance(DownloadState.Checking, "Checking", now, DownloadState.Queued, DownloadState.ResolvingMetadata);

    /// <summary>
    /// (pre-transfer) → Downloading. Returns true only on the first start (guarded by
    /// <see cref="StartedAt"/>) so <c>DownloadStarted</c> is emitted exactly once — a later resume
    /// goes through <see cref="Resume"/>, which does not re-emit.
    /// </summary>
    public bool MarkDownloading(DateTimeOffset now)
    {
        var advanced = Advance(
            DownloadState.Downloading, "Downloading", now,
            DownloadState.Queued, DownloadState.ResolvingMetadata, DownloadState.Checking);
        if (advanced)
        {
            StartedAt ??= now;
        }

        return advanced;
    }

    /// <summary>
    /// (any pre-transfer state)/Downloading/Checking → Completed. Records where the content landed.
    /// <para>
    /// A task may legitimately observe a finished torrent without ever transferring a byte: the
    /// engine is idempotent per info-hash, so a release re-selected after an import failure resolves
    /// to a torrent it already holds complete and reports it finished on the very first snapshot.
    /// Refusing that transition strands the task in <see cref="DownloadState.Queued"/> with no
    /// timeout and no failure — nothing else could ever move it — and its acquisition goal with it.
    /// </para>
    /// </summary>
    public bool MarkCompleted(string contentPath, DateTimeOffset now)
    {
        var advanced = Advance(
            DownloadState.Completed, "Completed", now,
            DownloadState.Queued, DownloadState.ResolvingMetadata,
            DownloadState.Downloading, DownloadState.Checking);
        if (advanced)
        {
            ContentPath = Text.Truncate(contentPath, PathMaxLength);
        }

        return advanced;
    }

    /// <summary>Completed → Seeding (a seeding policy is active and keeps the task uploading).</summary>
    public bool MarkSeeding(DateTimeOffset now) =>
        Advance(DownloadState.Seeding, "Seeding", now, DownloadState.Completed);

    /// <summary>
    /// Any non-terminal state → Error. Returns true only on the first fatal error so
    /// <c>DownloadFailed</c> is emitted once; a recoverable engine warning must not call this.
    /// </summary>
    public bool Fail(string reason, DateTimeOffset now)
    {
        if (IsTerminal || State is DownloadState.Removed)
        {
            return false;
        }

        LastError = Text.Truncate(reason, ReasonMaxLength);
        RecordTransition(DownloadState.Error, "Failed", now, reason);
        return true;
    }

    // -- control transitions (user/API driven; strict) -------------------------------------------

    /// <summary>Holds an in-flight task. Rejected once the task has completed or been removed.</summary>
    /// <exception cref="DownloadStateConflictException">The task is past the point a pause applies to.</exception>
    public void Pause(DateTimeOffset now)
    {
        EnsureCanPause();
        if (State is DownloadState.Paused)
        {
            return;
        }

        RecordTransition(DownloadState.Paused, "Pause", now, null);
    }

    /// <summary>
    /// The check <see cref="Pause"/> makes, on its own, so a caller can make it before it touches the
    /// engine: a pause the row then refused would already have stopped a seeding torrent, and answered
    /// the operator with a fault.
    /// </summary>
    /// <exception cref="DownloadStateConflictException">The task is past the point a pause applies to.</exception>
    public void EnsureCanPause()
    {
        if (!IsInFlight)
        {
            throw new DownloadStateConflictException(Id, $"it is {State}, and only a download in progress can be paused");
        }
    }

    /// <summary>
    /// Resumes a held task back to Downloading (does not re-emit <c>DownloadStarted</c>).
    /// </summary>
    /// <param name="force">
    /// Whether the caller may override a network hold. False for the ordinary operator action, which
    /// must not be able to resume a download into a network that is leaking; true only for the
    /// deliberate override the <c>PauseAndAlert</c> policy allows, and the override is recorded.
    /// </param>
    /// <exception cref="NetworkHoldException">The task is held and <paramref name="force"/> is false.</exception>
    /// <exception cref="DownloadStateConflictException">The task is not one a resume applies to.</exception>
    public void Resume(DateTimeOffset now, bool force = false)
    {
        EnsureCanResume(force);
        if (NetworkHoldSince is not null)
        {
            // An override clears the hold, so the task is no longer a candidate for the automatic
            // release and cannot be resumed twice by a tunnel that recovers later.
            NetworkHoldSince = null;
            NetworkHoldReason = null;
            RecordTransition(DownloadState.Downloading, "NetworkHoldOverride", now, "resumed by operator while egress was unverified");
            RestartStallClock(now);
            return;
        }

        if (State is DownloadState.Downloading)
        {
            return;
        }

        RecordTransition(DownloadState.Downloading, "Resume", now, null);
        RestartStallClock(now);
    }

    /// <summary>The checks <see cref="Resume"/> makes, on their own, for a caller about to touch the engine.</summary>
    /// <exception cref="NetworkHoldException">The task is held and <paramref name="force"/> is false.</exception>
    /// <exception cref="DownloadStateConflictException">The task is not one a resume applies to.</exception>
    public void EnsureCanResume(bool force)
    {
        if (NetworkHoldSince is not null)
        {
            if (!force)
            {
                throw new NetworkHoldException(Id, NetworkHoldReason ?? "unknown");
            }

            return;
        }

        if (State is not (DownloadState.Paused or DownloadState.Downloading))
        {
            throw new DownloadStateConflictException(Id, $"it is {State}, and only a paused download can be resumed");
        }
    }

    // -- network hold (tunnel-driven; the operator does not decide it, the observation does) --------

    /// <summary>
    /// Holds an in-flight task because torrent egress could not be verified. Returns false when the
    /// task is already held or is not in flight, which is what makes a redelivered transition a no-op.
    /// <para>
    /// A hold is never a failure. Emitting <c>DownloadFailed</c> here would send the acquisition goal
    /// back to searching for the duration of the outage — burning indexer quota to find releases it
    /// then could not fetch either — and would lose the transfer's progress with it.
    /// </para>
    /// </summary>
    public bool HoldForNetwork(string reason, DateTimeOffset now)
    {
        if (NetworkHoldSince is not null || !IsInFlight)
        {
            return false;
        }

        NetworkHoldSince = now;
        NetworkHoldReason = Text.Truncate(reason, ReasonMaxLength);

        // Already Paused (an operator paused it before the tunnel went) keeps that state and simply
        // gains the hold, so releasing the hold cannot silently resume something a person stopped.
        if (State is not DownloadState.Paused)
        {
            RecordTransition(DownloadState.Paused, "NetworkHold", now, NetworkHoldReason);
            return true;
        }

        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Releases a network hold once egress is verified again. Returns false when the task was not
    /// held, so a redelivered restoration changes nothing.
    /// <para>
    /// A task that was already paused by hand before the hold went on stays paused: the hold is
    /// lifted, the state is not. Resuming it would silently undo a person's decision.
    /// </para>
    /// </summary>
    public bool ReleaseNetworkHold(DateTimeOffset now)
    {
        if (NetworkHoldSince is null)
        {
            return false;
        }

        var pausedByHold = LastPauseWasTheHold();

        NetworkHoldSince = null;
        NetworkHoldReason = null;

        if (pausedByHold && State is DownloadState.Paused)
        {
            RecordTransition(DownloadState.Downloading, "NetworkRelease", now, null);
            RestartStallClock(now);
        }
        else
        {
            UpdatedAt = now;
        }

        return true;
    }

    /// <summary>
    /// Whether the most recent move into <see cref="DownloadState.Paused"/> was the hold rather than
    /// a person. Read from the history trail instead of a second column, because the trail is already
    /// the authority on how the task reached the state it is in — and it cannot drift from itself.
    /// </summary>
    private bool LastPauseWasTheHold()
    {
        for (var index = History.Count - 1; index >= 0; index--)
        {
            if (History[index].ToState is DownloadState.Paused)
            {
                return History[index].Trigger == "NetworkHold";
            }
        }

        return false;
    }

    /// <summary>True while this task is stopped because egress could not be verified.</summary>
    [NotMapped]
    public bool IsNetworkHeld => NetworkHoldSince is not null;

    /// <summary>Removes the task from the engine (terminal). Idempotent.</summary>
    public void Remove(DateTimeOffset now)
    {
        if (State is DownloadState.Removed)
        {
            return;
        }

        RecordTransition(DownloadState.Removed, "Remove", now, null);
    }

    /// <summary>
    /// Ends a seeding task whose seeding rule is met. The torrent leaves the engine as on a removal,
    /// but the history says why, so a rule doing its job is not mistaken for an operator's decision.
    /// </summary>
    public void EndSeeding(DateTimeOffset now)
    {
        if (State is not DownloadState.Seeding)
        {
            return;
        }

        RecordTransition(DownloadState.Removed, "SeedingDone", now, "the seeding rule was met");
    }

    /// <summary>
    /// Closes a finished task whose torrent the engine no longer holds — the sidecar restarted, and a
    /// finished torrent is not re-added. Returns false for any other task. Without it the row said
    /// <c>Seeding</c> for good while nothing was, and every checkpoint pass failed on it again.
    /// </summary>
    public bool EndSeedingLost(DateTimeOffset now)
    {
        if (State is not (DownloadState.Completed or DownloadState.Seeding))
        {
            return false;
        }

        RecordTransition(DownloadState.Removed, "SeedingLost", now, "the engine no longer holds this torrent, so it stopped seeding");
        return true;
    }

    // -- stall clock -----------------------------------------------------------------------------

    /// <summary>
    /// Folds one snapshot into the stall clock. The clock restarts whenever the transfer moved, and
    /// also whenever it was not expected to: a paused or held task, or a torrent the engine keeps in its
    /// own queue, is waiting rather than stalled, and must not come out of the wait already timed out.
    /// </summary>
    public void TrackProgress(double progress, bool engineWaiting, DateTimeOffset now)
    {
        if (progress > BestProgress)
        {
            BestProgress = progress;
            RestartStallClock(now);
            return;
        }

        if (LastProgressAt is null || engineWaiting || !CanStall || NetworkHoldSince is not null)
        {
            RestartStallClock(now);
        }
    }

    /// <summary>True when the task has been trying and failing to move for at least <paramref name="timeout"/>.</summary>
    public bool HasStalled(TimeSpan timeout, DateTimeOffset now) =>
        CanStall && NetworkHoldSince is null && LastProgressAt is { } last && now - last >= timeout;

    /// <summary>
    /// The states in which a transfer is expected to be moving. <see cref="DownloadState.Checking"/>
    /// is left out: hashing a large payload on a slow disk moves nothing the swarm can see.
    /// </summary>
    private bool CanStall =>
        State is DownloadState.Queued or DownloadState.ResolvingMetadata or DownloadState.Downloading;

    private void RestartStallClock(DateTimeOffset now) => LastProgressAt = now;

    // -- non-transition updates ------------------------------------------------------------------

    /// <summary>Refreshes the live transfer figures. No state change, no history line (high volume).</summary>
    public void UpdateProgress(
        double progress,
        long downloadRate,
        long uploadRate,
        int numPeers,
        int numSeeds,
        long allTimeUpload,
        long allTimeDownload,
        int seedingSeconds,
        DateTimeOffset now)
    {
        Progress = progress;
        DownloadRate = downloadRate;
        UploadRate = uploadRate;
        NumPeers = numPeers;
        NumSeeds = numSeeds;
        AllTimeUpload = allTimeUpload;
        AllTimeDownload = allTimeDownload;
        SeedingSeconds = seedingSeconds;
        UpdatedAt = now;
    }

    /// <summary>Stores a fresh resume-data checkpoint and returns its sequence number.</summary>
    public int SaveCheckpoint(byte[] resumeData, DateTimeOffset now)
    {
        ResumeData = resumeData;
        CheckpointSeq += 1;
        UpdatedAt = now;
        return CheckpointSeq;
    }

    public void ApplySeedingPolicy(SeedingPolicy policy, DateTimeOffset now)
    {
        SeedingPolicyJson = policy.ToJson();
        UpdatedAt = now;
    }

    /// <summary>Replaces the known file layout (from ListFiles / metadata resolution).</summary>
    public void SetFiles(IEnumerable<(int Index, string Path, long Size, FilePriorityLevel Priority)> entries)
    {
        Files.Clear();
        foreach (var entry in entries)
        {
            Files.Add(new TorrentFile
            {
                DownloadTaskId = Id,
                Index = entry.Index,
                Path = Text.Truncate(entry.Path, PathMaxLength)!,
                Size = entry.Size,
                Priority = entry.Priority,
            });
        }
    }

    /// <summary>Updates the selected priority of the named files (others are left unchanged).</summary>
    public void SetFilePriorities(IReadOnlyDictionary<int, FilePriorityLevel> priorities, DateTimeOffset now)
    {
        foreach (var file in Files)
        {
            if (priorities.TryGetValue(file.Index, out var priority))
            {
                file.Priority = priority;
            }
        }

        UpdatedAt = now;
    }

    /// <summary>True when the task is seeding and its policy's ratio/time bound is met.</summary>
    public bool ShouldStopSeeding() =>
        State is DownloadState.Seeding && Policy.IsMet(AllTimeUpload, AllTimeDownload, SeedingSeconds);

    // -- machinery -------------------------------------------------------------------------------

    private bool Advance(DownloadState to, string trigger, DateTimeOffset now, params DownloadState[] validFrom)
    {
        if (State == to || Array.IndexOf(validFrom, State) < 0)
        {
            return false;
        }

        RecordTransition(to, trigger, now, null);
        return true;
    }

    private void RecordTransition(DownloadState to, string trigger, DateTimeOffset now, string? note)
    {
        var seq = History.Count == 0 ? 0 : History[^1].Seq + 1;
        History.Add(new DownloadHistoryRecord
        {
            DownloadTaskId = Id,
            Seq = seq,
            FromState = State,
            ToState = to,
            Trigger = Text.Truncate(trigger, 40)!,
            OccurredAt = now,
            Note = Text.Truncate(note, ReasonMaxLength),
        });
        State = to;
        UpdatedAt = now;
    }
}
