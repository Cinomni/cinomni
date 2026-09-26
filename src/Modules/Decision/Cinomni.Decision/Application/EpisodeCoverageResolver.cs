using Cinomni.Catalog.Contracts;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision.Application;

/// <summary>
/// Turns a parsed release's numbering into the concrete catalog units it covers, through the single
/// shared resolver (<see cref="ICatalogSeriesQuery"/>). No numbering rules live here: the parser
/// produces <see cref="EpisodeNumbering"/> and Catalog owns the mapping from numbers to episode ids.
/// <para>
/// Resolutions are memoised per instance. One search returns many candidates that share a numbering
/// (every quality of S02E05), and a naive implementation would issue one round-trip per candidate.
/// </para>
/// </summary>
internal sealed class EpisodeCoverageResolver(ICatalogSeriesQuery seriesQuery)
{
    /// <summary>
    /// Upper bound on the seasons a single multi-season pack may expand to. Indexer titles are
    /// hostile input: "S01-S99" must not become 99 database round-trips.
    /// </summary>
    private const int MaxSeasonsPerPack = 20;

    private readonly Dictionary<string, IReadOnlyList<Guid>> _memo = [];

    /// <summary>
    /// The requested units this release covers. Empty when the release has no numbering, the work is
    /// unknown, or nothing it covers was asked for.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ResolveAsync(
        Guid? workId,
        EpisodeNumbering? numbering,
        IReadOnlyList<Guid> requestedUnitIds,
        CancellationToken cancellationToken)
    {
        if (workId is not { } work || numbering is null)
        {
            return [];
        }

        var resolved = await ResolveMemoisedAsync(new WorkId(work), numbering, cancellationToken);
        if (requestedUnitIds.Count == 0)
        {
            return resolved;
        }

        // Only units that were actually asked for count as coverage: a pack that happens to contain
        // episodes nobody is missing is not a better answer than one that contains the missing ones.
        var requested = requestedUnitIds.ToHashSet();
        return resolved.Where(requested.Contains).ToList();
    }

    private async Task<IReadOnlyList<Guid>> ResolveMemoisedAsync(
        WorkId workId,
        EpisodeNumbering numbering,
        CancellationToken cancellationToken)
    {
        var key = MemoKey(workId, numbering);
        if (_memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = await ResolveNumberingAsync(workId, numbering, cancellationToken);
        _memo[key] = resolved;
        return resolved;
    }

    private async Task<IReadOnlyList<Guid>> ResolveNumberingAsync(
        WorkId workId,
        EpisodeNumbering numbering,
        CancellationToken cancellationToken)
    {
        if (numbering.AirDate is DateOnly airDate)
        {
            return Ids(await seriesQuery.ResolveByAirDateAsync(workId, airDate, cancellationToken));
        }

        if (numbering.AbsoluteEpisodes.Count > 0)
        {
            var absolute = new List<Guid>();
            foreach (var number in numbering.AbsoluteEpisodes)
            {
                absolute.AddRange(Ids(await seriesQuery.ResolveByAbsoluteNumberAsync(workId, number, cancellationToken)));
            }

            return absolute;
        }

        if (numbering.Season is not int season)
        {
            return [];
        }

        if (numbering.Episodes.Count > 0)
        {
            var first = numbering.Episodes.Min();
            var last = numbering.Episodes.Max();
            return first == last
                ? Ids(await seriesQuery.ResolveEpisodeAsync(workId, season, first, cancellationToken))
                : Ids(await seriesQuery.ResolveEpisodeRangeAsync(workId, season, first, last, cancellationToken));
        }

        // A pack: every episode of the season, or of each season in a multi-season range.
        var lastSeason = numbering.SeasonTo is int to && to > season
            ? Math.Min(to, season + MaxSeasonsPerPack - 1)
            : season;

        var packUnits = new List<Guid>();
        for (var current = season; current <= lastSeason; current++)
        {
            packUnits.AddRange(Ids(await seriesQuery.ResolveSeasonAsync(workId, current, cancellationToken)));
        }

        return packUnits;
    }

    private static IReadOnlyList<Guid> Ids(IReadOnlyList<EpisodeSummary> episodes) =>
        episodes.Select(e => e.Id.Value).ToList();

    private static string MemoKey(WorkId workId, EpisodeNumbering numbering) =>
        string.Join(
            '|',
            workId.Value,
            numbering.Season,
            numbering.SeasonTo,
            string.Join(',', numbering.Episodes),
            string.Join(',', numbering.AbsoluteEpisodes),
            numbering.AirDate);
}
