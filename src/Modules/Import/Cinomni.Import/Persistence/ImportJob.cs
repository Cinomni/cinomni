using System.ComponentModel.DataAnnotations.Schema;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Import.Persistence;

/// <summary>One file the import specs picked out of a download's content path, with its fingerprint.</summary>
public sealed record ImportFileCandidate(string Path, long Size, string? Hash);

/// <summary>
/// The Import aggregate root: the <b>persisted, recoverable</b> job that lands a completed
/// download into the library (cases #7/#8/#9/#15). Its finite-state machine is pure, guarded logic —
/// an illegal transition is rejected and leaves the state untouched — and every transition is
/// appended to the history trail. The defining property is recoverability: the job, its
/// <see cref="ImportFileMatch"/>es and its <see cref="FileOperation"/>s are persisted and idempotent,
/// so a crash mid-import resumes from the last confirmed step instead of re-duplicating.
/// <para>
/// The job machine is a <b>batch</b> machine: <c>Matching → Deciding → Operating → Probing →
/// Registered</c> is traversed <em>once</em> for the whole set of files, and the per-file lifecycle
/// lives on <see cref="ImportFileMatch"/>. That is not a stylistic choice — every transition here
/// throws on a state mismatch, so looping <c>Approve</c>/<c>MarkOperated</c>/<c>Register</c> over N
/// files would throw on the second one. A movie is simply the batch of one, and the
/// <see cref="MatchedFilePath"/>/<see cref="TargetPath"/>/<see cref="AssetId"/> members are kept as
/// first-match shims so the observability surface is unchanged for it.
/// </para>
/// </summary>
public sealed class ImportJob
{
    private const int PathMaxLength = 2048;
    private const int ReasonMaxLength = 500;
    private const int TriggerMaxLength = 40;
    private const int NoteMaxLength = 500;
    private const int HashMaxLength = 128;

    public Guid Id { get; init; }

    /// <summary>The completed download this job imports (one job per download, used for correlation).</summary>
    public Guid DownloadTaskId { get; init; }

    /// <summary>The acquisition intent this job serves (echoed in our events so the goal advances).</summary>
    public Guid IntentId { get; init; }

    /// <summary>The acquisition attempt this job serves (echoed in our events).</summary>
    public Guid AttemptId { get; init; }

    /// <summary>The catalog work this asset belongs to (opaque correlation, carried into MediaAvailable).</summary>
    public Guid WorkId { get; init; }

    /// <summary>The monitored target this asset satisfies (opaque correlation, carried into MediaAvailable).</summary>
    public Guid TargetId { get; init; }

    public ImportJobState State { get; private set; }

    /// <summary>The staging location handed over by the completed download (content root to scan).</summary>
    public required string SourcePath { get; init; }

    /// <summary>First-match shim over <see cref="Matches"/> — the source of truth is the child.</summary>
    public string? MatchedFilePath { get; private set; }

    /// <summary>First-match shim over <see cref="Matches"/> — the source of truth is the child.</summary>
    public long MatchedFileSize { get; private set; }

    /// <summary>First-match shim over <see cref="Matches"/> — the source of truth is the child.</summary>
    public string? MatchedFileHash { get; private set; }

    /// <summary>First-landed-match shim: where the (first) file landed in the library.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>First-landed-match shim: the (first) registered asset's identity.</summary>
    public Guid? AssetId { get; private set; }

    /// <summary>First-landed-match shim: the (first) file's serialized <see cref="MediaInfo"/> (jsonb).</summary>
    public string? MediaInfoJson { get; private set; }

    /// <summary>Why the job was rejected/unmatched/failed, if it was.</summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// The catalog units the acquisition asked this download to satisfy, as the hand-off carried them
    /// (jsonb). Persisted because it is the one thing a re-drive cannot reconstruct: matching is
    /// constrained to this scope, and a retry that lost it would land the extra episodes a season pack
    /// happens to ship — assets nobody asked for, against targets nobody is monitoring.
    /// Null on a job written before the scope was persisted, and on a manual import that never had one.
    /// </summary>
    public string? RequestedUnitsJson { get; private set; }

    /// <summary>
    /// How many times startup recovery has handed this job back to the processor. Monotone, and the
    /// suffix of the retry command's idempotency key: without it the second retry of the same job
    /// would collide with the first and be dropped, which is the failure this counter exists to
    /// prevent. It is also the honest answer to "how many times has this import been tried".
    /// </summary>
    public int RecoveryAttempts { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The files this job landed (or tried to). One for a movie, N for a season pack.</summary>
    public List<ImportFileMatch> Matches { get; } = [];

    public List<FileOperation> Operations { get; } = [];

    public List<ImportHistoryRecord> History { get; } = [];

    /// <summary>The analysed media info of the first landed file, projected from <see cref="MediaInfoJson"/>.</summary>
    [NotMapped]
    public MediaInfo? MediaInfo => MediaInfoSerializer.FromJson(MediaInfoJson);

    /// <summary>
    /// The acquisition's unit scope, projected from <see cref="RequestedUnitsJson"/>. Null — rather
    /// than empty — when the job never recorded one, because the two mean different things to the
    /// resolver: an absent scope accepts whatever the files resolve to, an empty one would too but
    /// says the acquisition asked for nothing.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<Guid>? RequestedUnitIds => UnitScopeSerializer.FromJson(RequestedUnitsJson);

    /// <summary>
    /// True while this job still has work a re-drive could do: at least one file that never landed,
    /// or no file recorded at all (a crash between opening the job and matching its content). A
    /// Pending job whose every match already landed has nothing left to retry.
    /// </summary>
    [NotMapped]
    public bool HasUnfinishedWork => Matches.Count == 0 || Matches.Any(m => !m.IsLanded);

    /// <summary>
    /// How many times startup recovery will re-drive one job before parking it for a person. Five,
    /// which is the command queue's own attempt ceiling: a job that has survived five whole restarts
    /// without landing is not waiting on a transient condition.
    /// </summary>
    public const int MaxRecoveryAttempts = 5;

    /// <summary>Whether this job has spent its automatic re-drives (see <see cref="ExhaustRecovery"/>).</summary>
    [NotMapped]
    public bool IsRecoveryExhausted => RecoveryAttempts >= MaxRecoveryAttempts;

    /// <summary>A job is done once it registers, is rejected, or fails to match (the last two reopenable).</summary>
    public bool IsTerminal => State is ImportJobState.Registered or ImportJobState.Rejected;

    /// <summary>Creates a job in <see cref="ImportJobState.Pending"/> from a completed download.</summary>
    /// <param name="requestedUnitIds">
    /// The catalog units the acquisition asked for, recorded so a re-drive after a restart resolves
    /// files against the same scope the first drive did. Optional and trailing: a manual import has
    /// no acquisition scope.
    /// </param>
    public static ImportJob Create(
        Guid downloadTaskId,
        Guid intentId,
        Guid attemptId,
        Guid workId,
        Guid targetId,
        string sourcePath,
        DateTimeOffset now,
        IReadOnlyList<Guid>? requestedUnitIds = null)
    {
        var job = new ImportJob
        {
            Id = Uuid7.New(),
            DownloadTaskId = downloadTaskId,
            IntentId = intentId,
            AttemptId = attemptId,
            WorkId = workId,
            TargetId = targetId,
            State = ImportJobState.Pending,
            SourcePath = Text.Truncate(sourcePath, PathMaxLength)!,
            RequestedUnitsJson = UnitScopeSerializer.ToJson(requestedUnitIds),
            CreatedAt = now,
            UpdatedAt = now,
        };

        job.History.Add(new ImportHistoryRecord
        {
            ImportJobId = job.Id,
            Seq = 0,
            FromState = ImportJobState.Pending,
            ToState = ImportJobState.Pending,
            Trigger = "ProcessCompletedDownload",
            OccurredAt = now,
            Note = null,
        });

        return job;
    }

    /// <summary>Pending → Matching: start matching the content to the units it serves.</summary>
    public void BeginMatching(DateTimeOffset now) =>
        Transition(ImportJobState.Pending, ImportJobState.Matching, "BeginMatching", now, null);

    /// <summary>
    /// Matching → Deciding: the specs picked <paramref name="candidates"/> out of the content path.
    /// <b>Upserts by source path</b>: a re-drive reuses the existing match — and therefore its already
    /// minted, already registered asset id — instead of recording the file a second time.
    /// </summary>
    public IReadOnlyList<ImportFileMatch> RecordMatches(IReadOnlyList<ImportFileCandidate> candidates, DateTimeOffset now)
    {
        RequireState(ImportJobState.Matching, ImportJobState.Deciding);
        var recorded = UpsertMatches(candidates);
        ShimFirstMatch(recorded[0]);
        RecordTransition(ImportJobState.Deciding, "RecordMatch", now, Note(recorded));
        return recorded;
    }

    /// <summary>Single-file convenience over <see cref="RecordMatches"/> (the movie batch of one).</summary>
    public ImportFileMatch RecordMatch(string matchedFilePath, long size, string hash, DateTimeOffset now) =>
        RecordMatches([new ImportFileCandidate(matchedFilePath, size, hash)], now)[0];

    /// <summary>Matching → Unmatched: no acceptable file was found (queued for manual import).</summary>
    public void MarkUnmatched(string reason, DateTimeOffset now)
    {
        Reason = Text.Truncate(reason, ReasonMaxLength);
        Transition(ImportJobState.Matching, ImportJobState.Unmatched, "Unmatched", now, reason);
    }

    /// <summary>Deciding → Operating: the specs approved the batch; the per-file operations are planned next.</summary>
    public void Approve(DateTimeOffset now) =>
        Transition(ImportJobState.Deciding, ImportJobState.Operating, "Approve", now, null);

    /// <summary>
    /// Plans one recoverable operation for one matched file while the batch is in
    /// <see cref="ImportJobState.Operating"/>, and attaches it to the match so a later re-drive can
    /// tell an already-verified file from one that still has to be linked.
    /// </summary>
    public FileOperation PlanOperation(ImportFileMatch match, FileOperationType type, string targetPath)
    {
        if (State != ImportJobState.Operating)
        {
            throw new InvalidOperationException(
                $"A file operation may only be planned while Operating (job {Id} is {State}).");
        }

        var operation = FileOperation.Plan(Id, Operations.Count, type, match.SourcePath, targetPath);
        Operations.Add(operation);
        match.AttachOperation(operation.Seq, targetPath);
        return operation;
    }

    /// <summary>Deciding → Rejected: import specs rejected the batch (explained, reopenable via ManualImport).</summary>
    public void Reject(string reason, DateTimeOffset now)
    {
        Reason = Text.Truncate(reason, ReasonMaxLength);
        Transition(ImportJobState.Deciding, ImportJobState.Rejected, "Reject", now, reason);
    }

    /// <summary>Operating → Probing: at least one file landed and verified. Records where the first one landed.</summary>
    public void MarkOperated(DateTimeOffset now)
    {
        RequireState(ImportJobState.Operating, ImportJobState.Probing);
        var landed = Matches.FirstOrDefault(m => m.IsLanded);
        TargetPath = Text.Truncate(landed?.TargetPath, PathMaxLength);
        RecordTransition(ImportJobState.Probing, "Operated", now, TargetPath);
    }

    /// <summary>
    /// Operating → Pending: no file landed. The job is not lost — it returns to Pending to retry,
    /// and the files that already verified keep their state so the retry does not
    /// re-link them.
    /// </summary>
    public void MarkOperationFailed(string reason, DateTimeOffset now)
    {
        Reason = Text.Truncate(reason, ReasonMaxLength);
        Transition(ImportJobState.Operating, ImportJobState.Pending, "OperationFailed", now, reason);
    }

    /// <summary>
    /// Probing → Pending: part of the batch landed and was registered, part did not. The batch is
    /// not complete, so the job is not <see cref="ImportJobState.Registered"/>; it is not lost
    /// either — it returns to Pending, and a re-drive replans only the files that failed because the
    /// registered ones are already <see cref="ImportFileMatch.IsLanded"/>.
    /// </summary>
    public void MarkPartiallyRegistered(string reason, DateTimeOffset now)
    {
        Reason = Text.Truncate(reason, ReasonMaxLength);
        Transition(ImportJobState.Probing, ImportJobState.Pending, "PartiallyRegistered", now, reason);
    }

    /// <summary>
    /// Probing → Registered: the whole batch is done. A file whose probe failed still registers
    /// (with empty streams), per the FSM's probe-failure rule — the streams can be re-probed later.
    /// Returns the files <b>this call</b> announced, so the caller publishes one <c>MediaAvailable</c>
    /// per newly registered file and never re-announces one an earlier drive already did.
    /// </summary>
    public IReadOnlyList<ImportFileMatch> Register(DateTimeOffset now)
    {
        RequireState(ImportJobState.Probing, ImportJobState.Registered);
        var announced = RegisterProbedFiles();
        RecordTransition(ImportJobState.Registered, "Register", now, Note(RegisteredMatches()));
        return announced;
    }

    /// <summary>
    /// Announces every probed file under the asset id minted when it was first matched, <b>without
    /// closing the batch</b>, and returns the files this call announced.
    /// <para>
    /// A season pack that loses one file must still register the nine that landed. Withholding them
    /// leaves real files inside the library tree that no module — not Library, not Catalog, not
    /// Monitoring — has ever heard of: orphans the platform will happily re-download, and which no
    /// later drive can announce either, because the file is already on disk and verified.
    /// </para>
    /// </summary>
    public IReadOnlyList<ImportFileMatch> RegisterProbedFiles()
    {
        var announced = Matches.Where(m => m.State == ImportFileMatchState.Probed).ToList();
        foreach (var match in announced)
        {
            match.MarkRegistered();
        }

        var registered = RegisteredMatches();
        AssetId = registered.Count == 0 ? null : registered[0].AssetId;
        MediaInfoJson = registered.Count == 0 ? null : registered[0].MediaInfoJson;
        return announced;
    }

    /// <summary>
    /// Claims this job for one startup re-drive and returns the attempt number that claim earned.
    /// The state does not move — the job is and stays <see cref="ImportJobState.Pending"/> until the
    /// drive itself runs — but the trail records that the platform decided to try again, and why the
    /// job was reachable at all.
    /// <para>
    /// The returned number is what makes the retry command's idempotency key unique per attempt, so
    /// the increment is claimed <b>before</b> the command is queued and is committed with it. The two
    /// are not, however, all-or-nothing: a queue that drops the key answers no rather than failing, and
    /// the attempt number is then already represented by a live command — which costs one number and
    /// nothing else.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The job is not Pending, or it has no re-drives left (<see cref="IsRecoveryExhausted"/>).
    /// </exception>
    public int BeginRecovery(DateTimeOffset now)
    {
        if (State is not ImportJobState.Pending)
        {
            throw new InvalidOperationException(
                $"Only a Pending import job can be re-driven by recovery (job {Id} is {State}).");
        }

        if (IsRecoveryExhausted)
        {
            throw new InvalidOperationException(
                $"Import job {Id} has spent its {MaxRecoveryAttempts} recovery attempts.");
        }

        RecoveryAttempts += 1;
        RecordTransition(ImportJobState.Pending, "RecoverPending", now, $"restart re-drive {RecoveryAttempts}");
        return RecoveryAttempts;
    }

    /// <summary>
    /// Pending → Unmatched: the platform has stopped trying to re-drive this job on its own.
    /// <para>
    /// Something has to say so. A job that can never land — a permission the process does not have, a
    /// disk with no room, a target path that keeps colliding — is otherwise re-driven at every start
    /// for the life of the installation: another history line, another queued command and another full
    /// scan and hardlink attempt over the staging tree each time, with nothing in the state to
    /// distinguish "will never succeed" from "waiting to be tried again".
    /// </para>
    /// <para>
    /// <see cref="ImportJobState.Unmatched"/> and not a new terminal state, because this is exactly
    /// what that state already means to the rest of the product: done being driven automatically, and
    /// reopenable by a person through <see cref="ManualImport"/>. The reason is persisted, so what an
    /// operator is shown is why it stopped rather than that it stopped.
    /// </para>
    /// </summary>
    public void ExhaustRecovery(string reason, DateTimeOffset now)
    {
        Reason = Text.Truncate(reason, ReasonMaxLength);
        Transition(ImportJobState.Pending, ImportJobState.Unmatched, "RecoveryExhausted", now, reason);
    }

    /// <summary>Unmatched → Deciding: a manual import supplied the file to land (reopens the job).</summary>
    public void ManualImport(string matchedFilePath, long size, string hash, DateTimeOffset now)
    {
        RequireState(ImportJobState.Unmatched, ImportJobState.Deciding);
        var recorded = UpsertMatches([new ImportFileCandidate(matchedFilePath, size, hash)]);
        ShimFirstMatch(recorded[0]);
        RecordTransition(ImportJobState.Deciding, "ManualImport", now, matchedFilePath);
    }

    private IReadOnlyList<ImportFileMatch> UpsertMatches(IReadOnlyList<ImportFileCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("An import job cannot record an empty set of files.", nameof(candidates));
        }

        var recorded = new List<ImportFileMatch>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var existing = Matches.FirstOrDefault(
                m => string.Equals(m.SourcePath, candidate.Path, StringComparison.Ordinal));
            if (existing is not null)
            {
                // Reuse the row — and therefore the asset id it already minted (and may already have
                // registered). Re-minting here is what breaks ux_media_versions_full_path on a retry.
                existing.RefreshFingerprint(candidate.Size, candidate.Hash);
                existing.Reopen();
                recorded.Add(existing);
                continue;
            }

            var match = ImportFileMatch.Plan(Id, Matches.Count, candidate.Path, candidate.Size, candidate.Hash);
            Matches.Add(match);
            recorded.Add(match);
        }

        return recorded;
    }

    private IReadOnlyList<ImportFileMatch> RegisteredMatches() =>
        [.. Matches.Where(m => m.State == ImportFileMatchState.Registered)];

    private void ShimFirstMatch(ImportFileMatch match)
    {
        MatchedFilePath = Text.Truncate(match.SourcePath, PathMaxLength);
        MatchedFileSize = match.Size;
        MatchedFileHash = Text.Truncate(match.Hash, HashMaxLength);
    }

    private static string Note(IReadOnlyList<ImportFileMatch> matches) => matches.Count switch
    {
        0 => "no files",
        1 => matches[0].SourcePath,
        _ => $"{matches.Count} files",
    };

    private void Transition(ImportJobState from, ImportJobState to, string trigger, DateTimeOffset now, string? note)
    {
        RequireState(from, to);
        RecordTransition(to, trigger, now, note);
    }

    /// <summary>Guards a transition <b>before</b> anything is mutated, so a rejected one changes nothing.</summary>
    private void RequireState(ImportJobState from, ImportJobState to)
    {
        if (State != from)
        {
            throw new InvalidOperationException(
                $"Illegal import transition {State}→{to} (expected from {from}) for job {Id}.");
        }
    }

    private void RecordTransition(ImportJobState to, string trigger, DateTimeOffset now, string? note)
    {
        var seq = History.Count == 0 ? 0 : History[^1].Seq + 1;
        History.Add(new ImportHistoryRecord
        {
            ImportJobId = Id,
            Seq = seq,
            FromState = State,
            ToState = to,
            Trigger = Text.Truncate(trigger, TriggerMaxLength)!,
            OccurredAt = now,
            Note = Text.Truncate(note, NoteMaxLength),
        });
        State = to;
        UpdatedAt = now;
    }
}
