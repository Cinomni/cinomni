using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// Materialises a series' <c>Season</c> and <c>Episode</c> targets from the catalog structure and applies
/// the root's <see cref="MonitoringMode"/> cascade to them.
/// <para>
/// Two rules govern this class:
/// </para>
/// <list type="number">
/// <item>
/// <b>It never deletes and never duplicates.</b> Targets are upserted by their catalog unit id
/// (<c>ux_monitored_targets_work_ref</c>); a concurrent run loses the race on that constraint and
/// converges on the winner's rows.
/// </item>
/// <item>
/// <b>A structure-driven run cascades onto new rows only.</b> Series structure arrives asynchronously and
/// grows season by season, so re-running it must not undo a per-episode toggle the user made in between.
/// Only an explicit policy change (<c>ApplyMonitoringPolicy</c>) re-cascades over the whole subtree.
/// </item>
/// </list>
/// </summary>
public sealed class SeriesTargetMaterializer(
    MonitoringDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ICatalogSeriesQuery seriesQuery,
    ILogger<SeriesTargetMaterializer> logger)
{
    /// <summary>
    /// Brings the work's target tree in line with its catalog structure.
    /// <paramref name="reapplyCascade"/> re-evaluates the mode over targets that already exist.
    /// </summary>
    public async Task MaterializeAsync(
        Guid workId,
        bool reapplyCascade,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await MaterializeOnceAsync(workId, reapplyCascade, cancellationToken);
        }
        catch (DbUpdateException ex) when (TargetEvents.IsDuplicateTarget(ex))
        {
            // A concurrent sync of the same work won the race on ux_monitored_targets_work_ref. Drop our
            // pending inserts and run again: the second pass finds the winner's rows and updates them.
            logger.LogInformation(
                "Concurrent series-target sync for work {WorkId}; converging on the existing rows.", workId);
            dbContext.ChangeTracker.Clear();
            await MaterializeOnceAsync(workId, reapplyCascade, cancellationToken);
        }
    }

    private async Task MaterializeOnceAsync(Guid workId, bool reapplyCascade, CancellationToken cancellationToken)
    {
        var targets = await dbContext.MonitoredTargets
            .Where(t => t.WorkId == workId)
            .ToListAsync(cancellationToken);

        var root = targets.Find(t => t.Kind == TargetKind.Series);
        if (root is null)
        {
            // The root is created by ApplyMonitoringPolicy off WorkAdded. Arriving here means the
            // structure event overtook it; the next sync (or policy apply) materialises the tree.
            logger.LogInformation(
                "No series root for work {WorkId}; skipping target materialisation until the policy lands.", workId);
            return;
        }

        var seasons = await seriesQuery.GetSeasonsAsync(new WorkId(workId), cancellationToken);
        if (seasons.Count == 0)
        {
            logger.LogDebug("Work {WorkId} has no catalog seasons yet; nothing to materialise.", workId);
            return;
        }

        var shape = SeriesShape.From(seasons.Select(s => s.Number));
        var byUnit = IndexByUnit(targets);
        var enabled = new List<MonitoredTarget>();

        foreach (var season in seasons)
        {
            var episodes = await seriesQuery.GetEpisodesAsync(new WorkId(workId), season.Number, cancellationToken);
            var seasonTarget = UpsertSeason(root, season, byUnit, targets);
            var seasonEpisodes = new List<MonitoredTarget>(episodes.Count);

            foreach (var episode in episodes)
            {
                seasonEpisodes.Add(
                    UpsertEpisode(root, seasonTarget, episode, shape, reapplyCascade, byUnit, targets, enabled));
            }

            ApplySeasonState(seasonTarget, seasonEpisodes, enabled);
        }

        TargetRollup.Recompute(targets);
        await CommitAsync(enabled, cancellationToken);
    }

    /// <summary>Finds or creates the target watching one season, and refreshes its descriptive fields.</summary>
    private MonitoredTarget UpsertSeason(
        MonitoredTarget root,
        SeasonSummary season,
        Dictionary<Guid, MonitoredTarget> byUnit,
        List<MonitoredTarget> targets)
    {
        var target = Resolve(byUnit, targets, root, season.Id.Value, TargetKind.Season);
        target.ParentTargetId = root.Id;
        target.SeasonNumber = season.Number;
        target.EpisodeTitle = Text.Truncate(season.Title, MonitoredTarget.TitleMaxLength);
        target.AirDate = MonitoringModePolicy.AirInstant(season.AirDate, airDateTime: null);
        target.PublishedAirDate = season.AirDate;
        target.Mode = root.Mode;
        return target;
    }

    /// <summary>
    /// Finds or creates the target watching one episode, refreshes its numbering/air data and applies the
    /// cascade — always for a row this pass created, and for an existing row only when the caller asked
    /// for a re-apply.
    /// </summary>
    private MonitoredTarget UpsertEpisode(
        MonitoredTarget root,
        MonitoredTarget seasonTarget,
        EpisodeSummary episode,
        SeriesShape shape,
        bool reapplyCascade,
        Dictionary<Guid, MonitoredTarget> byUnit,
        List<MonitoredTarget> targets,
        List<MonitoredTarget> enabled)
    {
        var existed = byUnit.ContainsKey(episode.Id.Value);
        var target = Resolve(byUnit, targets, root, episode.Id.Value, TargetKind.Episode);

        target.ParentTargetId = seasonTarget.Id;
        target.SeasonNumber = episode.SeasonNumber;
        target.EpisodeNumber = episode.Number;
        target.AbsoluteNumber = episode.AbsoluteNumber;
        target.AirDate = MonitoringModePolicy.AirInstant(episode.AirDate, episode.AirDateTime);
        // The gate instant and the published date are two different things, and the search criterion
        // needs the published one. Carrying it costs a column; re-deriving it costs a day.
        target.PublishedAirDate = episode.AirDate;
        target.EpisodeTitle = Text.Truncate(episode.Title, MonitoredTarget.TitleMaxLength);
        target.Mode = root.Mode;

        // Catalog is authoritative on availability: an episode that already has an asset is never missing,
        // whatever this module believed before.
        if (episode.HasAsset)
        {
            target.IsMissing = false;
        }
        else if (!existed)
        {
            target.IsMissing = true;
        }

        if (!existed || reapplyCascade)
        {
            SetMonitored(
                target,
                MonitoringModePolicy.ShouldMonitor(root.Mode, episode, shape, DateTimeOffset.UtcNow),
                enabled);
        }

        return target;
    }

    /// <summary>
    /// A season is watched while any of its episodes is: it exists to be the vehicle for a season-pack
    /// search, not to hold a policy of its own. Its missing flag is settled by <see cref="TargetRollup"/>.
    /// </summary>
    private static void ApplySeasonState(
        MonitoredTarget seasonTarget,
        List<MonitoredTarget> episodes,
        List<MonitoredTarget> enabled)
    {
        if (episodes.Count > 0)
        {
            SetMonitored(seasonTarget, episodes.Exists(e => e.Monitored), enabled);
        }
    }

    /// <summary>Applies a monitored flag, recording the enable edge so exactly one event is published for it.</summary>
    private static void SetMonitored(MonitoredTarget target, bool monitored, List<MonitoredTarget> enabled)
    {
        if (monitored && !target.Monitored && target.IsAcquirable)
        {
            enabled.Add(target);
        }

        target.Monitored = monitored;
    }

    /// <summary>Returns the target watching <paramref name="unitId"/>, creating and tracking it if absent.</summary>
    private MonitoredTarget Resolve(
        Dictionary<Guid, MonitoredTarget> byUnit,
        List<MonitoredTarget> targets,
        MonitoredTarget root,
        Guid unitId,
        TargetKind kind)
    {
        if (byUnit.TryGetValue(unitId, out var existing))
        {
            return existing;
        }

        var target = new MonitoredTarget
        {
            Id = Uuid7.New(),
            WorkId = root.WorkId,
            Kind = kind,
            TargetRef = unitId,
            Mode = root.Mode,
            IsMissing = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.MonitoredTargets.Add(target);
        byUnit[unitId] = target;
        targets.Add(target);
        return target;
    }

    /// <summary>The upsert and every enable announcement it produced commit together.</summary>
    private Task CommitAsync(List<MonitoredTarget> enabled, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            foreach (var target in enabled)
            {
                await eventBus.PublishAsync(TargetEvents.Enabled(target), token);
            }
        }, cancellationToken);

    private static Dictionary<Guid, MonitoredTarget> IndexByUnit(List<MonitoredTarget> targets)
    {
        var byUnit = new Dictionary<Guid, MonitoredTarget>(targets.Count);
        foreach (var target in targets)
        {
            byUnit.TryAdd(target.TargetRef, target);
        }

        return byUnit;
    }
}
