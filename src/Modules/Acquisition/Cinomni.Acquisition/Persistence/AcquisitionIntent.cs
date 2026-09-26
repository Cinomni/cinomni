using Cinomni.Acquisition.Contracts;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Acquisition.Persistence;

/// <summary>
/// The Acquisition aggregate root: the <b>persistent goal</b> of getting content for a
/// monitored target, across many attempts (case #6). Its finite-state machine is pure,
/// guarded logic — an illegal transition is rejected and leaves the state untouched — and every
/// transition is appended to the history trail. The defining invariant lives in
/// <see cref="MarkDownloadFailed"/> / <see cref="MarkImportFailed"/>: a failure returns the goal to
/// <see cref="IntentState.Searching"/> instead of dropping it, until attempts are exhausted.
/// </summary>
public sealed class AcquisitionIntent
{
    private const int TriggerMaxLength = 40;
    private const int NoteMaxLength = 500;
    private const int ReasonMaxLength = 500;
    private const int ReleaseGuidMaxLength = 500;
    private const int DownloadUrlMaxLength = 2048;

    public Guid Id { get; init; }

    /// <summary>The monitored target this goal serves (one open intent per target).</summary>
    public Guid TargetId { get; init; }

    /// <summary>The catalog work, carried for the success/failure events (cross-context id).</summary>
    public Guid WorkId { get; init; }

    /// <summary>
    /// The catalog unit this goal is for — the work id for a movie, the season or episode id for a
    /// series leaf — echoed from <c>MonitoringEnabled</c>. This is what lets an import satisfy the
    /// nine sibling goals a season pack closed without any of them ever opening an attempt. Null on
    /// a goal created before unit correlation existed.
    /// </summary>
    public Guid? UnitId { get; init; }

    public string Mode { get; init; } = "All";

    public IntentState State { get; private set; }

    /// <summary>How many attempts have been opened; the guard for exhaustion.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>How many attempts the goal may open before it gives up; a manual retry extends it.</summary>
    public int MaxAttempts { get; private set; }

    /// <summary>The release guid of the attempt currently in flight, if any.</summary>
    public string? SelectedReleaseGuid { get; private set; }

    public string? LastFailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public List<AcquisitionAttempt> Attempts { get; } = [];

    public List<AcquisitionHistoryRecord> History { get; } = [];

    /// <summary>A goal accepts a new candidate only while it is actively searching (or reopened for upgrade).</summary>
    public bool CanSelectCandidate => State is IntentState.Searching or IntentState.Available;

    /// <summary>
    /// True once the goal has spent every attempt. Unlike a goal that is merely busy downloading or
    /// importing, this one will never accept a candidate again on its own, so a release aimed at it
    /// must be offered to another goal rather than held back.
    /// </summary>
    public bool HasGivenUp => State is IntentState.Exhausted or IntentState.Cancelled;

    /// <summary>Creates a goal in <see cref="IntentState.Requested"/> (born from Monitoring/Request).</summary>
    public static AcquisitionIntent Create(
        Guid targetId,
        Guid workId,
        string mode,
        DateTimeOffset now,
        int maxAttempts,
        Guid? unitId = null)
    {
        var intent = new AcquisitionIntent
        {
            Id = Uuid7.New(),
            TargetId = targetId,
            WorkId = workId,
            UnitId = unitId,
            Mode = Text.Truncate(mode, 50) ?? "All",
            MaxAttempts = maxAttempts,
            State = IntentState.Requested,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Genesis marker ([*] → Requested).
        intent.History.Add(new AcquisitionHistoryRecord
        {
            IntentId = intent.Id,
            Seq = 0,
            FromState = IntentState.Requested,
            ToState = IntentState.Requested,
            Trigger = "CreateAcquisitionIntent",
            OccurredAt = now,
            Note = null,
        });

        return intent;
    }

    /// <summary>Requested → Planned → Searching. Planning is folded into one step for the movie slice.</summary>
    public void Plan(DateTimeOffset now)
    {
        Transition(IntentState.Requested, IntentState.Planned, "PlanAcquisition", now);
        RecordTransition(IntentState.Searching, "PlanAcquisition", now, "ready to search");
    }

    /// <summary>
    /// Searching → CandidateSelected → Downloading: accepts a candidate chosen by Decision, opens a
    /// fresh attempt, and returns it so the caller can hand the download off. Rejected unless the
    /// goal is searchable (guard <see cref="CanSelectCandidate"/>).
    /// </summary>
    public AcquisitionAttempt SelectCandidate(
        Guid evaluationId, string releaseGuid, string downloadUrl, DateTimeOffset now, AttemptRelease? release = null)
    {
        if (!CanSelectCandidate)
        {
            throw new InvalidOperationException(
                $"Cannot select a candidate: intent {Id} is {State}, expected Searching or Available.");
        }

        RecordTransition(IntentState.CandidateSelected, "ReleaseSelected", now, $"release {releaseGuid}");
        SelectedReleaseGuid = Text.Truncate(releaseGuid, ReleaseGuidMaxLength);

        AttemptCount += 1;
        var attempt = new AcquisitionAttempt
        {
            Id = Uuid7.New(),
            IntentId = Id,
            Ordinal = AttemptCount,
            EvaluationId = evaluationId,
            ReleaseGuid = Text.Truncate(releaseGuid, ReleaseGuidMaxLength)!,
            DownloadUrl = Text.Truncate(downloadUrl, DownloadUrlMaxLength)!,
            ReleaseTitle = Text.Truncate(release?.Title, AcquisitionAttempt.ReleaseTitleMaxLength),
            IndexerName = Text.Truncate(release?.IndexerName, AcquisitionAttempt.IndexerNameMaxLength),
            // A negative count is not a count; unknown stays unknown.
            Seeders = release?.Seeders is < 0 ? 0 : release?.Seeders,
            Leechers = release?.Leechers is < 0 ? 0 : release?.Leechers,
            State = AttemptState.Started,
            StartedAt = now,
        };
        Attempts.Add(attempt);

        RecordTransition(IntentState.Downloading, "AssignDownload", now, $"attempt {attempt.Ordinal}");
        return attempt;
    }

    /// <summary>⇠ DownloadStarted: the in-flight attempt began transferring. Intent stays Downloading.</summary>
    public void MarkDownloadStarted(DateTimeOffset now)
    {
        RequireState(IntentState.Downloading, "DownloadStarted");
        CurrentAttempt().MarkDownloading();
        UpdatedAt = now;
    }

    /// <summary>⇠ DownloadCompleted: Downloading → Importing (awaiting Import to land the file).</summary>
    public void MarkDownloadCompleted(DateTimeOffset now) =>
        Transition(IntentState.Downloading, IntentState.Importing, "DownloadCompleted", now);

    /// <summary>
    /// ⇠ DownloadFailed: closes the attempt and — the core invariant — returns the goal to
    /// <see cref="IntentState.Searching"/> if attempts remain, or exhausts it. The intent survives.
    /// </summary>
    public void MarkDownloadFailed(string reason, DateTimeOffset now)
    {
        RequireState(IntentState.Downloading, "DownloadFailed");
        CurrentAttempt().Close(AttemptState.FailedDownload, reason, now);
        RetryOrExhaust(reason, now);
    }

    /// <summary>⇠ ImportCompleted: Importing → Available. The goal is met.</summary>
    public void MarkImported(DateTimeOffset now)
    {
        RequireState(IntentState.Importing, "ImportCompleted");
        CurrentAttempt().Close(AttemptState.Imported, null, now);
        RecordTransition(IntentState.Available, "ImportCompleted", now, null);
    }

    /// <summary>⇠ ImportFailed: closes the attempt and retries or exhausts, like a download failure.</summary>
    public void MarkImportFailed(string reason, DateTimeOffset now)
    {
        RequireState(IntentState.Importing, "ImportFailed");
        CurrentAttempt().Close(AttemptState.FailedImport, reason, now);
        RetryOrExhaust(reason, now);
    }

    /// <summary>
    /// An administrator asked to try again now. An <see cref="IntentState.Exhausted"/> goal is reopened
    /// to <see cref="IntentState.Searching"/> with <paramref name="extraAttempts"/> more to spend — the
    /// spent attempts stay on record, so the history still says what failed. A goal that is already
    /// searching needs no change; the caller only has to ask for a search.
    /// </summary>
    /// <returns>True when the goal was reopened, false when it was already searching.</returns>
    /// <exception cref="InvalidOperationException">
    /// The goal is busy with a download or import of its own, or already has its content: retrying it
    /// would open a second attempt beside one still in flight, or replace content nobody asked to replace.
    /// </exception>
    public bool RetryNow(DateTimeOffset now, int extraAttempts)
    {
        if (State == IntentState.Searching)
        {
            return false;
        }

        if (State != IntentState.Exhausted)
        {
            throw new InvalidOperationException(
                $"Cannot retry intent {Id}: it is {State}; only a searching or exhausted goal can be retried.");
        }

        MaxAttempts = AttemptCount + Math.Max(1, extraAttempts);
        RecordTransition(IntentState.Searching, "ManualRetry", now, $"{MaxAttempts - AttemptCount} more attempts");
        return true;
    }

    /// <summary>
    /// The work this goal serves was removed from the catalog: whatever state it is in, it ends in
    /// <see cref="IntentState.Cancelled"/>. An attempt still open is closed with <paramref name="reason"/>,
    /// so the history says why it stopped rather than leaving it in flight for ever.
    /// </summary>
    /// <returns>False when the goal was already cancelled, which a redelivery finds.</returns>
    public bool Cancel(DateTimeOffset now, string reason)
    {
        if (State == IntentState.Cancelled)
        {
            return false;
        }

        if (Attempts.Count > 0 && Attempts[^1].State is AttemptState.Started or AttemptState.Downloading)
        {
            Attempts[^1].Close(AttemptState.FailedDownload, reason, now);
        }

        SelectedReleaseGuid = null;
        RecordTransition(IntentState.Cancelled, "WorkRemoved", now, reason);
        return true;
    }

    /// <summary>⇠ UpgradeDesired: Available → Searching to look for a better release (cutoff not met).</summary>
    public void Reopen(DateTimeOffset now) =>
        Transition(IntentState.Available, IntentState.Searching, "UpgradeDesired", now);

    /// <summary>
    /// Searching|CandidateSelected → Available: the goal's content landed through <em>somebody
    /// else's</em> attempt (one season pack meets the season goal and every episode goal inside it).
    /// <b>No attempt is consumed</b> — the goal never tried anything, so charging it a retry would
    /// walk ten untouched episode goals towards <see cref="IntentState.Exhausted"/>.
    /// </summary>
    /// <returns>
    /// True when the goal moved. False — never an exception — when it is already met, already has a
    /// download of its own in flight, or is exhausted: the import fan-out is at-least-once and
    /// arrives at goals in every state.
    /// </returns>
    public bool MarkSatisfiedExternally(Guid assetId, DateTimeOffset now)
    {
        if (State is not (IntentState.Searching or IntentState.CandidateSelected))
        {
            return false;
        }

        RecordTransition(IntentState.Available, "SatisfiedExternally", now, $"asset {assetId}");
        SelectedReleaseGuid = null;
        return true;
    }

    private void RetryOrExhaust(string reason, DateTimeOffset now)
    {
        LastFailureReason = Text.Truncate(reason, ReasonMaxLength);
        SelectedReleaseGuid = null;

        if (AttemptCount >= MaxAttempts)
        {
            RecordTransition(IntentState.Exhausted, "Exhausted", now, reason);
            return;
        }

        // The goal is NOT lost — it goes back to searching for another candidate (case #6).
        RecordTransition(IntentState.Searching, "Retry", now, reason);
    }

    private AcquisitionAttempt CurrentAttempt() =>
        Attempts.Count > 0
            ? Attempts[^1]
            : throw new InvalidOperationException($"Intent {Id} has no attempt to act on.");

    private void RequireState(IntentState expected, string trigger)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(
                $"Cannot apply {trigger}: intent {Id} is {State}, expected {expected}.");
        }
    }

    private void Transition(IntentState from, IntentState to, string trigger, DateTimeOffset now, string? note = null)
    {
        if (State != from)
        {
            throw new InvalidOperationException(
                $"Illegal acquisition transition {State}→{to} (expected from {from}) for intent {Id}.");
        }

        RecordTransition(to, trigger, now, note);
    }

    private void RecordTransition(IntentState to, string trigger, DateTimeOffset now, string? note)
    {
        var seq = History.Count == 0 ? 0 : History[^1].Seq + 1;
        History.Add(new AcquisitionHistoryRecord
        {
            IntentId = Id,
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
