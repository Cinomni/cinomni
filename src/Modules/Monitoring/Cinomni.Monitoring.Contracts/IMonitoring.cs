using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Results;

namespace Cinomni.Monitoring.Contracts;

/// <summary>Public commands of the Monitoring module.</summary>
public interface IMonitoringCommands
{
    /// <summary>
    /// Applies a monitoring policy to a work, creating its target tree if absent: one row for a movie,
    /// a <c>Series</c> root plus a <c>Season</c> row per catalog season and an <c>Episode</c> row per
    /// catalog episode for a series. Idempotent; emits <c>MonitoringEnabled</c> only for acquirable
    /// targets that transition into a monitored state. Fails if the work is unknown to the Catalog.
    /// <para>
    /// This is the <em>explicit</em> policy change, so it re-applies the mode's cascade over the whole
    /// existing subtree — unlike the structure-driven sync, which only cascades onto rows it creates
    /// and therefore never undoes a per-episode toggle.
    /// </para>
    /// </summary>
    Task<Result<MonitoredTargetId>> ApplyMonitoringPolicyAsync(
        WorkId workId,
        MonitoringMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables monitoring for a single target, leaving its <c>Mode</c> alone (a mode of
    /// <c>None</c> is promoted to <c>All</c> on the enable edge, since "watch nothing" and "watch this"
    /// cannot both be true). The web client calls this on every toggle, so writing the mode here would
    /// destroy a user's <c>FirstSeason</c> or <c>Future</c> policy with one click.
    /// </summary>
    Task<Result> SetTargetMonitoredAsync(
        MonitoredTargetId targetId,
        bool monitored,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables a target <em>and every descendant</em> (a series root cascades to its seasons
    /// and their episodes; a season cascades to its episodes). Publishes one <c>MonitoringEnabled</c>
    /// per newly enabled acquirable target — never for the <c>Series</c> root, which is not acquirable.
    /// </summary>
    Task<Result> SetSubtreeMonitoredAsync(
        MonitoredTargetId targetId,
        bool monitored,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the "last searched at" stamp of a work, or of one season of it, so the next sweep treats
    /// its targets as due again. This is the whole of a manual search trigger: the sweep still applies the
    /// quota, the granularity choice and the unaired gate, so a user cannot use it to hammer an indexer.
    /// Fails only when the work has no targets at all.
    /// </summary>
    Task<Result> ClearSearchCooldownAsync(
        WorkId workId,
        int? seasonNumber = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same manual trigger, named by one target: a movie clears its work, an episode or season clears
    /// the season it belongs to — the granularity the sweep itself decides a pack or an episode at, so
    /// clearing only the leaf could leave its season's stamp holding the search back.
    /// Fails only when no such target exists.
    /// </summary>
    Task<Result> ClearTargetSearchCooldownAsync(
        MonitoredTargetId targetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves what a search for one target would ask for. Only Monitoring can answer it: the criterion is
/// built from the work's title, year and external ids plus the target's own numbering, and the units are
/// its subtree. Published so an interactive search runs the <em>same</em> query the sweep would, rather
/// than a second, silently divergent one.
/// </summary>
public interface ITargetSearchPlans
{
    /// <summary>The plan for one target, or null when the target is unknown or its work has vanished.</summary>
    Task<TargetSearchPlan?> ResolveAsync(
        MonitoredTargetId targetId,
        CancellationToken cancellationToken = default);
}

/// <summary>Public read model of the Monitoring module.</summary>
public interface IMonitoringQuery
{
    /// <summary>Whether the work has a monitored target (the predicate other modules ask for).</summary>
    Task<bool> IsMonitoredAsync(WorkId workId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The work's <em>root</em> target — the <c>Movie</c> row or the <c>Series</c> policy root, never a
    /// season or episode. Exactly one object, which is the shape the work detail page depends on.
    /// </summary>
    Task<MonitoredTargetSummary?> GetByWorkAsync(WorkId workId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every target of one work — root, seasons and episodes — ordered root first, then by season and
    /// episode number. The rollup counters on the root and the season rows are populated here.
    /// </summary>
    Task<IReadOnlyList<MonitoredTargetSummary>> ListByWorkAsync(WorkId workId, CancellationToken cancellationToken = default);

    /// <summary>The season targets of one work, ordered by season number, with their rollup counters.</summary>
    Task<IReadOnlyList<MonitoredTargetSummary>> ListSeasonsAsync(WorkId workId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Monitored targets still lacking an asset — the candidates for a missing search. Bounded: a single
    /// series contributes hundreds of rows, so an unbounded list is a memory hazard, not a convenience.
    /// <paramref name="offset"/> exists so the scoped browse can walk the table in bounded scan batches
    /// while narrowing to what a caller may see, without pulling every row into memory to filter it.
    /// </summary>
    Task<IReadOnlyList<MonitoredTargetSummary>> ListMissingAsync(
        int limit = MonitoringPaging.DefaultPageSize,
        int offset = 0,
        WorkId? workId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Episodes whose published air date falls in <paramref name="from"/>..<paramref name="to"/>
    /// inclusive. A row materialised before the published date was stored falls back to its gate instant.
    /// Season and series roots are excluded: a season premiere is not an airing of a file. Movies are
    /// excluded because no release date reaches Monitoring for them.
    /// </summary>
    Task<IReadOnlyList<MonitoredTargetSummary>> ListAiringAsync(
        DateOnly from,
        DateOnly to,
        int limit = MonitoringPaging.DefaultPageSize,
        int offset = 0,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MonitoredTargetSummary>>([]);

    /// <summary>Every monitored target, paginated by creation order.</summary>
    Task<IReadOnlyList<MonitoredTargetSummary>> ListAsync(
        int limit = MonitoringPaging.DefaultPageSize,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The targets watching the given catalog units (<c>TargetRef</c> ∈ <paramref name="unitIds"/>) — the
    /// reverse of the correlation the acquisition spine carries. An empty input yields an empty list.
    /// </summary>
    Task<IReadOnlyList<MonitoredTargetSummary>> ResolveTargetsForUnitsAsync(
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default);
}
