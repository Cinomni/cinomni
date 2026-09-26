using Cinomni.Kernel.Messaging;

namespace Cinomni.Decision.Contracts;

/// <summary>Stable registered names of the Decision integration events.</summary>
public static class DecisionEventNames
{
    public const string ReleaseEvaluated = "decision.release-evaluated";
    public const string ReleaseSelected = "decision.release-selected";
    public const string NoAcceptableRelease = "decision.no-acceptable-release";
    public const string UpgradeAssessed = "decision.upgrade-assessed";
}

/// <summary>
/// A release was evaluated against a profile. Persisted (outbox) for explainability, not only
/// consumption; the full reasons are read via <see cref="IReleaseEvaluationQuery"/>.
/// Carries the evaluator version so a consumer knows how the verdict was reached.
/// </summary>
public sealed record ReleaseEvaluated(
    Guid EvaluationId,
    Guid? TargetId,
    string ReleaseGuid,
    string Verdict,
    int CustomFormatScore,
    string EvaluatorVersion)
    : DomainEvent
{
    public override string IdempotencyKey => $"release-evaluated:{EvaluationId}";
}

/// <summary>
/// The best acceptable release was selected (Decision recommends; Acquisition executes). Carries
/// the download link and the originating target for correlation.
/// </summary>
/// <param name="UnitIds">
/// The catalog units the selected release covers (one episode, a whole season pack, or a single
/// movie). Trailing optional: this payload is persisted as jsonb, so an in-flight 1.x row
/// deserializes with it null and the consumer falls back to routing by <paramref name="TargetId"/>.
/// </param>
/// <param name="Release">
/// What the release was when it was chosen — title, indexer, swarm — so the goal can say what it is
/// trying without asking Decision again. Trailing optional for the same jsonb reason; null on a 1.x row.
/// </param>
public sealed record ReleaseSelected(
    Guid EvaluationId,
    Guid? TargetId,
    string ReleaseGuid,
    string DownloadUrl,
    IReadOnlyList<Guid>? UnitIds = null,
    SelectedReleaseInfo? Release = null)
    : DomainEvent
{
    public override string IdempotencyKey => $"release-selected:{EvaluationId}";
}

/// <summary>What a selected release was, as the indexer described it at the moment it was chosen.</summary>
/// <param name="Seeders">Seeders the indexer reported, or null when it did not say.</param>
/// <param name="Leechers">Leechers the indexer reported, or null when it did not say.</param>
public sealed record SelectedReleaseInfo(string Title, string IndexerName, int? Seeders, int? Leechers);

/// <summary>No candidate from a search passed the profile — nothing to acquire this round.</summary>
public sealed record NoAcceptableRelease(
    Guid SearchId,
    Guid? TargetId,
    int CandidatesEvaluated)
    : DomainEvent
{
    public override string IdempotencyKey => $"no-acceptable-release:{SearchId}";
}

/// <summary>
/// Decision has judged what an import landed against the profile's cutoff and says, per catalog unit,
/// whether it is still worth looking for something better. Emitted after every registration, so the
/// answer is refreshed by the very upgrade that may have settled the question.
/// <para>
/// Only Decision can answer this: it takes the acquisition profile, which no other module reads. It is
/// announced rather than asked for because the answer changes rarely and is needed often — the sweep
/// consults it on every tick, and a synchronous call per target would put Decision in that loop.
/// </para>
/// </summary>
/// <param name="AssetId">The registration that prompted the assessment; also what makes it idempotent.</param>
/// <param name="UnitsWantingUpgrade">Units whose current file sits below the cutoff.</param>
/// <param name="UnitsSatisfied">
/// Units that have reached it, or that upgrades do not apply to. Carried explicitly because the
/// interesting transition is the second one: without it, a title that has just been upgraded to the
/// cutoff would keep its "look for better" flag and be searched every week for ever.
/// </param>
public sealed record UpgradeAssessed(
    Guid AssetId,
    Guid WorkId,
    IReadOnlyList<Guid> UnitsWantingUpgrade,
    IReadOnlyList<Guid> UnitsSatisfied)
    : DomainEvent
{
    public override string IdempotencyKey => $"upgrade-assessed:{AssetId}";
}
