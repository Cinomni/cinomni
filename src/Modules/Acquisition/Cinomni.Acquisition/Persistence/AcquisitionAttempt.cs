using Cinomni.Acquisition.Contracts;

namespace Cinomni.Acquisition.Persistence;

/// <summary>
/// One immutable attempt within an intent (append-only): the release that was tried and
/// how it ended. A failure closes the attempt in its terminal state — it is never mutated back —
/// while the parent intent opens a fresh attempt (case #6).
/// </summary>
public sealed class AcquisitionAttempt
{
    public Guid Id { get; init; }

    public Guid IntentId { get; init; }

    /// <summary>1-based position of this attempt within its intent.</summary>
    public int Ordinal { get; init; }

    /// <summary>The Decision evaluation that selected this candidate (correlation + idempotency).</summary>
    public Guid EvaluationId { get; init; }

    public required string ReleaseGuid { get; init; }

    public required string DownloadUrl { get; init; }

    public const int ReleaseTitleMaxLength = 500;

    public const int IndexerNameMaxLength = 200;

    /// <summary>
    /// What the release was when it was chosen: title, indexer and swarm as the indexer reported them.
    /// Kept so the attempt can say what it tried; null on an attempt opened before this was recorded.
    /// </summary>
    public string? ReleaseTitle { get; init; }

    public string? IndexerName { get; init; }

    public int? Seeders { get; init; }

    public int? Leechers { get; init; }

    public AttemptState State { get; internal set; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the attempt reached a terminal state (imported or failed).</summary>
    public DateTimeOffset? ClosedAt { get; internal set; }

    public string? FailureReason { get; internal set; }

    /// <summary>The catalog units this attempt claims (one for a movie or episode, N for a pack).</summary>
    public List<AcquisitionAttemptUnit> Units { get; } = [];

    internal void ClaimUnits(IEnumerable<Guid> unitIds)
    {
        foreach (var unitId in unitIds.Distinct())
        {
            Units.Add(new AcquisitionAttemptUnit { AttemptId = Id, UnitId = unitId });
        }
    }

    internal void MarkDownloading() => State = AttemptState.Downloading;

    internal void Close(AttemptState terminal, string? reason, DateTimeOffset now)
    {
        State = terminal;
        FailureReason = Text.Truncate(reason, 500);
        ClosedAt = now;
    }
}
