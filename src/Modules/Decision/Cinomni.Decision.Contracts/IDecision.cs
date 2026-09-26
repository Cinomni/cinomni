using Cinomni.Kernel.Results;

namespace Cinomni.Decision.Contracts;

/// <summary>Minimal projection of an acquisition profile.</summary>
/// <param name="CutoffRank">
/// The quality rank at which a title is good enough and stops being searched. Trailing optional so
/// existing construction sites are unaffected.
/// </param>
/// <param name="UpgradesAllowed">Whether a title already on disk may be replaced by a better release.</param>
public sealed record AcquisitionProfileSummary(
    AcquisitionProfileId Id,
    string Name,
    int MinFormatScore,
    int CutoffRank = 0,
    bool UpgradesAllowed = false);

/// <summary>Read model of the configured profiles.</summary>
public interface IProfileQuery
{
    Task<IReadOnlyList<AcquisitionProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Changing what a profile considers good enough. Separate from the read model because it is the only
/// write surface a profile has: everything else about a profile is seeded, and editing the allowed
/// qualities and format rules is its own piece of work.
/// </summary>
public interface IProfileAdministration
{
    /// <summary>
    /// Sets the cutoff and whether upgrades happen at all. Both together, because they are one decision:
    /// the answer to "should this library replace what it already has, and up to what point".
    /// </summary>
    Task<bool> SetUpgradePolicyAsync(
        Guid profileId,
        int cutoffRank,
        bool upgradesAllowed,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read model of past evaluations, with their persisted reasons — this is what powers the
/// "interactive search shows why each release was accepted or rejected" requirement.
/// </summary>
public interface IReleaseEvaluationQuery
{
    Task<IReadOnlyList<ReleaseEvaluationSummary>> GetForTargetAsync(
        Guid targetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Search on demand and choose by hand. The automatic pipeline runs on a cadence and takes the best
/// candidate it can justify; this is the surface for the two cases it cannot serve — "search now,
/// because I know something aired" and "take that one, even though the profile said no".
/// <para>
/// The two halves are deliberately separate calls. A search costs an indexer round trip and decides
/// nothing; the grab decides, and a person is what happens in between.
/// </para>
/// </summary>
public interface IInteractiveSearch
{
    /// <summary>
    /// Runs a federated search for one monitored target and evaluates every candidate against the
    /// profile — persisting the reasons, announcing nothing, selecting nobody.
    /// </summary>
    /// <remarks>
    /// It bypasses the sweep's cooldown and quota on purpose: those govern how often the platform may
    /// ask an indexer unprompted. It does not bypass the profile, which still judges every candidate.
    /// </remarks>
    Task<Result<InteractiveSearchResult>> SearchAsync(
        Guid targetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands a chosen evaluation to Acquisition, whatever the profile made of it. The override is
    /// recorded as one more (append-only) reason on the evaluation, so the library can always answer
    /// why a file the profile rejected is on disk. Idempotent per evaluation and target.
    /// </summary>
    Task<Result<ManualSelection>> GrabAsync(
        Guid evaluationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Releases the sweep must not select. Blocking is an operator decision, separate from the profile's
/// verdict: a release the profile accepted can still be one the household does not want taken again.
/// </summary>
public interface IReleaseBlocklist
{
    /// <summary>
    /// Blocks the release behind an evaluation. The guid is read from that evaluation, not from the
    /// caller. A second block of the same release updates the reason rather than inserting another row.
    /// </summary>
    Task<Result<ReleaseBlock>> BlockAsync(
        Guid evaluationId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Lifts a block. Missing is success: the release is already eligible again.</summary>
    Task<Result> UnblockAsync(Guid blockId, CancellationToken cancellationToken = default);

    Task<ReleaseBlockList> ListAsync(CancellationToken cancellationToken = default);
}
