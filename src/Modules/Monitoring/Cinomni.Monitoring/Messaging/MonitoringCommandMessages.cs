using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;

namespace Cinomni.Monitoring.Messaging;

/// <summary>Stable registered names of the Monitoring commands.</summary>
public static class MonitoringCommandNames
{
    public const string ApplyPolicy = "monitoring.apply-policy";
    public const string EvaluateMissing = "monitoring.evaluate-missing";
    public const string SyncSeriesTargets = "monitoring.sync-series-targets";
    public const string MarkUnitsSatisfied = "monitoring.mark-units-satisfied";
    public const string SetUpgradeWanted = "monitoring.set-upgrade-wanted";
    public const string RemoveWorkTargets = "monitoring.remove-work-targets";
}

/// <summary>
/// Applies a monitoring policy to a work off the request path. Enqueued by the WorkAdded
/// reaction so the target write happens in its own transaction (the outbox relay dispatches
/// event handlers inside its own); idempotent by <c>apply-policy:{workId}</c>.
/// </summary>
/// <remarks>
/// <see cref="InitialOnly"/> makes it the default a work starts with: applied only when the work has no
/// root yet, and otherwise a no-op. It defaults to false so a command queued before the field existed
/// still applies as it did.
/// </remarks>
public sealed record ApplyMonitoringPolicyCommand(Guid WorkId, MonitoringMode Mode, bool InitialOnly = false) : ICommand;

/// <summary>
/// Periodic sweep (job <c>monitoring.evaluate-missing</c>): requests a search for every
/// monitored target that still lacks an asset and is due, recreating the RSS/missing cadence.
/// Parameterless so the scheduler can instantiate it.
/// </summary>
public sealed record EvaluateMissingCommand : ICommand;

/// <summary>Deletes the targets of a work removed from the catalog (⇠ Catalog's <c>WorkRemoved</c>).</summary>
public sealed record RemoveWorkTargetsCommand(Guid WorkId, bool DeleteFiles = false) : ICommand;

/// <summary>
/// Materialises the season and episode targets of a series from the catalog structure a snapshot just
/// produced.
/// <para>
/// Enqueued with the key <c>sync-series-targets:{workId}:{snapshotId}</c> — deliberately <b>not</b>
/// <c>apply-policy:{workId}</c>, which <c>WorkAddedHandler</c> consumes forever
/// (<c>ux_command_idempotency_key</c> has no cleanup). Structure arrives asynchronously and grows with
/// every later season, so a work-only key would silently swallow every season after the first.
/// </para>
/// <para>
/// Carries the snapshot <em>pointer</em>, never the structure: the queue persists payloads as jsonb and a
/// 900-episode array does not belong in a command row.
/// </para>
/// </summary>
public sealed record SyncSeriesTargetsCommand(Guid WorkId, Guid SnapshotId) : ICommand;

/// <summary>
/// Clears <c>IsMissing</c> on the targets watching the catalog units an import just landed, then rolls the
/// change up episode → season → series.
/// <para>
/// Keyed <c>units-satisfied:{assetId}</c>: one asset lands once, and a redelivery of the same
/// <c>MediaAvailable</c> must not re-run the rollup. <see cref="UnitIds"/> is the content correlation the
/// import event carries; for a movie it is <c>[WorkId]</c>.
/// </para>
/// </summary>
public sealed record MarkUnitsSatisfiedCommand(
    Guid WorkId,
    Guid AssetId,
    IReadOnlyList<Guid> UnitIds) : ICommand;

/// <summary>
/// Records Decision's verdict on whether the targets of these units are worth another search for
/// something better. Both lists are applied, because clearing the flag is what stops a title that has
/// reached the cutoff from being searched every week for ever.
/// </summary>
public sealed record SetUpgradeWantedCommand(
    Guid WorkId,
    IReadOnlyList<Guid> UnitsWantingUpgrade,
    IReadOnlyList<Guid> UnitsSatisfied) : ICommand;
