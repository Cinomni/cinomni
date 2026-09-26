using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// The Monitoring read model. Every list is bounded — one series contributes a root, N seasons and
/// hundreds of episodes, so the movie-era unbounded <c>ToListAsync</c> would page a whole library into
/// memory for a single request.
/// </summary>
public sealed class MonitoringQuery(MonitoringDbContext dbContext) : IMonitoringQuery
{
    public Task<bool> IsMonitoredAsync(WorkId workId, CancellationToken cancellationToken = default) =>
        dbContext.MonitoredTargets
            .AsNoTracking()
            .AnyAsync(t => t.WorkId == workId.Value && t.Monitored, cancellationToken);

    public async Task<MonitoredTargetSummary?> GetByWorkAsync(WorkId workId, CancellationToken cancellationToken = default)
    {
        // Root only, deterministically ordered: an unordered FirstOrDefault over a work that now holds a
        // root plus every season and episode would hand back an arbitrary row.
        var root = await dbContext.MonitoredTargets
            .AsNoTracking()
            .Where(t => t.WorkId == workId.Value && (t.Kind == TargetKind.Movie || t.Kind == TargetKind.Series))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (root is null)
        {
            return null;
        }

        // A movie is its own tree, so it stays one query — the work detail page is on the hot path.
        if (root.Kind == TargetKind.Movie)
        {
            return ToSummary(root, rollups: null);
        }

        // The series root's counters are the whole work's, so the detail page renders "12 of 40 missing"
        // from the single object it already fetches.
        var family = await LoadWorkAsync(workId, cancellationToken);
        return ToSummary(root, Rollups(family));
    }

    public async Task<IReadOnlyList<MonitoredTargetSummary>> ListByWorkAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        var family = await LoadWorkAsync(workId, cancellationToken);
        var rollups = Rollups(family);

        return family
            .OrderBy(t => t.Kind == TargetKind.Series || t.Kind == TargetKind.Movie ? 0 : 1)
            .ThenBy(t => t.SeasonNumber ?? int.MinValue)
            .ThenBy(t => t.Kind == TargetKind.Season ? 0 : 1)
            .ThenBy(t => t.EpisodeNumber ?? int.MinValue)
            .Select(t => ToSummary(t, rollups))
            .ToList();
    }

    public async Task<IReadOnlyList<MonitoredTargetSummary>> ListSeasonsAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        var family = await LoadWorkAsync(workId, cancellationToken);
        var rollups = Rollups(family);

        return family
            .Where(t => t.Kind == TargetKind.Season)
            .OrderBy(t => t.SeasonNumber ?? int.MinValue)
            .Select(t => ToSummary(t, rollups))
            .ToList();
    }

    public async Task<IReadOnlyList<MonitoredTargetSummary>> ListMissingAsync(
        int limit = MonitoringPaging.DefaultPageSize,
        int offset = 0,
        WorkId? workId = null,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.MonitoredTargets
            .AsNoTracking()
            .Where(t => t.Monitored && t.IsMissing);

        if (workId is { } scope)
        {
            query = query.Where(t => t.WorkId == scope.Value);
        }

        var targets = await query
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Skip(offset < 0 ? 0 : offset)
            .Take(MonitoringPaging.Clamp(limit))
            .ToListAsync(cancellationToken);

        return targets.Select(t => ToSummary(t, rollups: null)).ToList();
    }

    public async Task<IReadOnlyList<MonitoredTargetSummary>> ListAiringAsync(
        DateOnly from,
        DateOnly to,
        int limit = MonitoringPaging.DefaultPageSize,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var fromInstant = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toExclusive = to == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // Episodes only: a movie target carries no air date (no provider date reaches Monitoring for
        // one), so asking for movies here only ever matched nothing.
        var targets = await dbContext.MonitoredTargets
            .AsNoTracking()
            .Where(t => t.Kind == TargetKind.Episode)
            .Where(t =>
                (t.PublishedAirDate != null && t.PublishedAirDate >= from && t.PublishedAirDate <= to)
                || (t.PublishedAirDate == null && t.AirDate >= fromInstant && t.AirDate < toExclusive))
            .OrderBy(t => t.PublishedAirDate)
            .ThenBy(t => t.AirDate)
            .ThenBy(t => t.SeasonNumber)
            .ThenBy(t => t.EpisodeNumber)
            .ThenBy(t => t.Id)
            .Skip(Math.Max(offset, 0))
            .Take(MonitoringPaging.Clamp(limit))
            .ToListAsync(cancellationToken);

        return targets.Select(t => ToSummary(t, rollups: null)).ToList();
    }

    public async Task<IReadOnlyList<MonitoredTargetSummary>> ListAsync(
        int limit = MonitoringPaging.DefaultPageSize,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var targets = await dbContext.MonitoredTargets
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Skip(offset < 0 ? 0 : offset)
            .Take(MonitoringPaging.Clamp(limit))
            .ToListAsync(cancellationToken);

        return targets.Select(t => ToSummary(t, rollups: null)).ToList();
    }

    public async Task<IReadOnlyList<MonitoredTargetSummary>> ResolveTargetsForUnitsAsync(
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default)
    {
        if (unitIds.Count == 0)
        {
            return [];
        }

        var distinct = unitIds.Distinct().ToList();
        var targets = await dbContext.MonitoredTargets
            .AsNoTracking()
            .Where(t => distinct.Contains(t.TargetRef))
            .ToListAsync(cancellationToken);

        return targets.Select(t => ToSummary(t, rollups: null)).ToList();
    }

    private Task<List<MonitoredTarget>> LoadWorkAsync(WorkId workId, CancellationToken cancellationToken) =>
        dbContext.MonitoredTargets
            .AsNoTracking()
            .Where(t => t.WorkId == workId.Value)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Episode counters per parent node, plus the work-wide totals under the empty key. Computed from the
    /// leaves in one pass: the episodes are the only rows that count, so a season and the root never
    /// double-count each other.
    /// </summary>
    private static Dictionary<Guid, (int Missing, int Total)> Rollups(List<MonitoredTarget> family)
    {
        var rollups = new Dictionary<Guid, (int Missing, int Total)>();
        var workTotal = 0;
        var workMissing = 0;

        foreach (var episode in family.Where(t => t.Kind == TargetKind.Episode))
        {
            workTotal++;
            workMissing += episode.IsMissing ? 1 : 0;

            if (episode.ParentTargetId is not { } parentId)
            {
                continue;
            }

            rollups.TryGetValue(parentId, out var counters);
            rollups[parentId] = (counters.Missing + (episode.IsMissing ? 1 : 0), counters.Total + 1);
        }

        foreach (var root in family.Where(t => t.IsRoot))
        {
            rollups[root.Id] = (workMissing, workTotal);
        }

        return rollups;
    }

    private static MonitoredTargetSummary ToSummary(
        MonitoredTarget target,
        Dictionary<Guid, (int Missing, int Total)>? rollups)
    {
        var counters = rollups is not null && rollups.TryGetValue(target.Id, out var found)
            ? found
            : (Missing: 0, Total: 0);

        return new MonitoredTargetSummary(
            new MonitoredTargetId(target.Id),
            new WorkId(target.WorkId),
            target.Kind,
            target.Monitored,
            target.Mode,
            target.IsMissing,
            target.TargetRef,
            target.ParentTargetId is { } parentId ? new MonitoredTargetId(parentId) : null,
            target.SeasonNumber,
            target.EpisodeNumber,
            target.AbsoluteNumber,
            target.AirDate,
            target.EpisodeTitle,
            counters.Missing,
            counters.Total,
            target.PublishedAirDate);
    }
}
