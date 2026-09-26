using Cinomni.Kernel.Messaging;

namespace Cinomni.Decision.Messaging;

/// <summary>Stable registered names of the Decision commands.</summary>
public static class DecisionCommandNames
{
    public const string EvaluateReleases = "decision.evaluate-releases";
    public const string AssessUpgrade = "decision.assess-upgrade";
    public const string PurgeEvaluations = "decision.purge-evaluations";
    public const string ExcludeReleaseForTarget = "decision.exclude-release-for-target";
}

/// <summary>
/// Ages out the evaluations that explain nothing: old rejections from sweeps that took no action.
/// Accepted verdicts and manually overridden ones are never removed. Parameterless — the scheduler
/// constructs it and the window is deployment configuration.
/// </summary>
public sealed record PurgeEvaluationsCommand : ICommand;

/// <summary>
/// Evaluates the candidates of a completed search off the request path (enqueued by the
/// SearchCompleted reaction). Idempotent by <c>evaluate-releases:{searchId}</c>.
/// </summary>
public sealed record EvaluateReleasesCommand(Guid SearchId, Guid? TargetId) : ICommand;

/// <summary>
/// Judges what an import landed against the profile's cutoff, unit by unit, and announces the verdict
/// (enqueued by the MediaAssetRegistered reaction). Idempotent by <c>assess-upgrade:{assetId}</c>.
/// </summary>
public sealed record AssessUpgradeCommand(Guid AssetId, Guid WorkId, IReadOnlyList<Guid> UnitIds) : ICommand;

/// <summary>
/// Stops offering a release to the goal it just failed (enqueued by the AcquisitionAttemptFailed
/// reaction). Idempotent by <c>exclude-release-for-target:{attemptId}</c>, and by its unique row.
/// </summary>
public sealed record ExcludeReleaseForTargetCommand(Guid TargetId, string ReleaseGuid, string Reason) : ICommand;
