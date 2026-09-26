using Cinomni.Catalog.Contracts;
using Cinomni.Library.Contracts;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// What Subtitles needs to know about the episode an asset plays: the series title a provider matches
/// shows on, the numbering that pins the episode, and the series IMDb id when the catalog knows one.
/// Every member is null for a movie, and <see cref="None"/> is the movie value.
/// </summary>
public sealed record EpisodeContext(
    string? SeriesTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? ParentImdbId)
{
    /// <summary>The context of a movie: no series, no numbering. Leaves the provider query as it shipped.</summary>
    public static readonly EpisodeContext None = new(null, null, null, null);
}

/// <summary>
/// Anti-corruption layer between a Library asset and Catalog's series vocabulary: turns the asset's
/// <c>UnitIds</c> — which the acquisition spine correlates on — into <c>SxxEyy</c> plus the series
/// title. Reads both modules by interface only.
/// <para>
/// A multi-episode file is <b>one</b> asset serving several units. The subtitle
/// query is built for the <em>first</em> episode the file covers: a provider cannot return one subtitle
/// spanning two episodes anyway, and the first one is the deterministic choice.
/// </para>
/// </summary>
public sealed class EpisodeContextResolver(ICatalogQuery catalog, ICatalogSeriesQuery series)
{
    public async Task<EpisodeContext> ResolveAsync(
        MediaAssetDetail asset,
        CancellationToken cancellationToken = default)
    {
        var workId = new WorkId(asset.Asset.WorkId);
        var unitIds = asset.UnitIds ?? [];

        // A movie's unit is its own work (the movie path is UnitIds = [WorkId]) — nothing to resolve.
        if (unitIds.Count == 0 || unitIds.All(id => id == asset.Asset.WorkId))
        {
            return EpisodeContext.None;
        }

        var work = await catalog.GetByIdAsync(workId, cancellationToken);
        if (work is null || work.Kind != WorkKind.Series)
        {
            return EpisodeContext.None;
        }

        var parentImdbId = work.ExternalIds
            .FirstOrDefault(e => e.Provider == MetadataProvider.Imdb)?.Value;

        var episodes = new List<EpisodeSummary>();
        foreach (var unitId in unitIds)
        {
            if (await series.GetEpisodeAsync(workId, new EpisodeId(unitId), cancellationToken) is { } episode)
            {
                episodes.Add(episode);
            }
        }

        var first = episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number).FirstOrDefault();

        // A series asset whose units resolve to no episode (a season unit, or a stale link): the series
        // title still beats the release name as a search term, but nothing pins an episode.
        return first is null
            ? new EpisodeContext(work.Title, null, null, parentImdbId)
            : new EpisodeContext(work.Title, first.SeasonNumber, first.Number, parentImdbId);
    }
}
