using Cinomni.Decision.Contracts;

namespace Cinomni.Decision.Persistence;

/// <summary>
/// The immutable, append-only record of evaluating one release against a profile. Kept
/// for explainability; its <see cref="Reasons"/> are what the interactive search surfaces.
/// </summary>
public sealed class ReleaseEvaluationRecord
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    /// <summary>The search whose candidates were evaluated (correlation + idempotency).</summary>
    public Guid SearchId { get; init; }

    /// <summary>The monitored target that triggered the search, when known.</summary>
    public Guid? TargetId { get; init; }

    public required string ReleaseGuid { get; init; }

    public required string ReleaseTitle { get; init; }

    public string? CanonicalKey { get; init; }

    public Verdict Verdict { get; init; }

    public int CustomFormatScore { get; init; }

    public required string EvaluatorVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public List<DecisionReasonRecord> Reasons { get; } = [];
}
