using Cinomni.Kernel.Identifiers;

namespace Cinomni.Decision.Contracts;

/// <summary>Stable internal identity of an acquisition profile (UUIDv7).</summary>
public readonly record struct AcquisitionProfileId(Guid Value)
{
    public static AcquisitionProfileId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable internal identity of a release evaluation (UUIDv7).</summary>
public readonly record struct ReleaseEvaluationId(Guid Value)
{
    public static ReleaseEvaluationId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>The verdict of evaluating a release against a profile.</summary>
public enum Verdict
{
    Accepted = 1,
    RejectedPermanent = 2,
    RejectedTemporary = 3,
}

/// <summary>Whether a decision reason passed or failed.</summary>
public enum ReasonOutcome
{
    Pass = 1,
    Fail = 2,
}

/// <summary>How a rejection should be treated: permanent (drop) or temporary (retry later).</summary>
public enum RejectionKind
{
    Permanent = 1,
    Temporary = 2,
}

/// <summary>
/// The typed conditions a custom format can test against a parsed release. Matching is OR within a
/// type and AND across types. Persisted as <em>text</em> in <c>format_conditions.type</c>, so members
/// may only ever be APPENDED with an explicit value.
/// </summary>
public enum FormatConditionType
{
    ReleaseTitle = 1,
    Source = 2,
    Resolution = 3,
    Language = 4,
    IndexerFlag = 5,
    ReleaseGroup = 6,
    Size = 7,
    Edition = 8,
    Year = 9,
    QualityModifier = 10,

    /// <summary>
    /// The parsed content kind (<c>Movie</c>, <c>SingleEpisode</c>, <c>MultiEpisode</c>,
    /// <c>SeasonPack</c>) — how a profile expresses "prefer a pack" or "never a multi-episode file".
    /// </summary>
    ReleaseType = 11,
}

/// <summary>
/// One line of a decision's explanation: the rule, the property it checked, the profile's
/// expectation, the release's actual value, and the outcome. Append-only and persisted.
/// </summary>
public sealed record EvaluationReason(
    string Rule,
    string? Property,
    string? ProfileValue,
    string? ActualValue,
    ReasonOutcome Outcome,
    RejectionKind? Rejection);

/// <summary>The explainable result of evaluating one release against a profile.</summary>
/// <param name="IndexerName">
/// Where the release came from, read back from the search that found it; null once that search's results
/// were purged, or for an older search than the history describes.
/// </param>
/// <param name="Seeders">Seeders the indexer reported, when it did and the search is still described.</param>
/// <param name="Leechers">Leechers the indexer reported, under the same conditions.</param>
public sealed record ReleaseEvaluationSummary(
    ReleaseEvaluationId Id,
    string ReleaseGuid,
    string ReleaseTitle,
    Verdict Verdict,
    int CustomFormatScore,
    IReadOnlyList<EvaluationReason> Reasons,
    string? IndexerName = null,
    int? Seeders = null,
    int? Leechers = null);

/// <summary>
/// One candidate of an interactive search: what the indexer offered, what the profile made of it, and
/// why. It is <see cref="ReleaseEvaluationSummary"/> plus the release facts a person needs in order to
/// override the verdict — size, seeders, age and which indexer supplied it.
/// </summary>
/// <param name="Protocol">The release transport, by name (<c>Torrent</c>, <c>Usenet</c>).</param>
/// <param name="EpisodeCoverage">
/// How many of the requested catalog units this release covers: 1 for a movie or single episode, N for
/// a pack. It is why a slightly worse pack can be the better answer for a season.
/// </param>
/// <param name="IsRecommended">
/// The candidate the automatic pipeline would have taken. Exactly one at most, and never a rejected
/// one: it marks the default, it does not make the choice.
/// </param>
public sealed record EvaluatedCandidate(
    ReleaseEvaluationId EvaluationId,
    string ReleaseGuid,
    string ReleaseTitle,
    string IndexerName,
    string Protocol,
    long SizeBytes,
    int? Seeders,
    DateTimeOffset? PublishedAt,
    Verdict Verdict,
    int CustomFormatScore,
    int EpisodeCoverage,
    bool IsRecommended,
    IReadOnlyList<EvaluationReason> Reasons,
    bool Blocked,
    Guid? BlockId,
    string? BlockReason,
    bool GrabOverridesVerdict,
    int? Leechers = null);

/// <summary>A release the sweep must not take, and why an operator said so.</summary>
public sealed record ReleaseBlock(
    Guid Id,
    string ReleaseGuid,
    string ReleaseTitle,
    string Reason,
    DateTimeOffset CreatedAt);

/// <param name="Truncated">True when the list was cut at the server cap, so a full page is not the whole set.</param>
public sealed record ReleaseBlockList(IReadOnlyList<ReleaseBlock> Blocks, bool Truncated);

/// <summary>
/// What one interactive search found: the execution it ran, what it asked an indexer for, and every
/// candidate with its verdict and reasons, best first.
/// </summary>
/// <param name="Label">What was searched for, as a person reads it ("Season 2", "S02E05", "Movie").</param>
public sealed record InteractiveSearchResult(
    Guid TargetId,
    Guid WorkId,
    Guid SearchId,
    string Term,
    string Label,
    IReadOnlyList<EvaluatedCandidate> Candidates);

/// <summary>The release a person chose by hand, echoed back so the UI can say what it just queued.</summary>
public sealed record ManualSelection(
    ReleaseEvaluationId EvaluationId,
    Guid TargetId,
    string ReleaseTitle,
    Verdict Verdict,
    bool OverrodeVerdict);
