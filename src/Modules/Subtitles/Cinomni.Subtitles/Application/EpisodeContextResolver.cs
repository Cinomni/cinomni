using Cinomni.Catalog.Contracts;
using Cinomni.Library.Contracts;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// What Subtitles needs to know about the title an asset plays. For an episode: the series title a
/// provider matches shows on, the numbering that pins the episode, and the series IMDb id when the
/// catalog knows one. For a movie those are null and the movie's own catalog title, year and IMDb id
/// are filled instead: what a candidate's identity is checked against. <see cref="None"/> knows nothing.
/// </summary>
public sealed record EpisodeContext(
    string? SeriesTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? ParentImdbId,
    string? MovieTitle = null,
    int? MovieYear = null,
    string? MovieImdbId = null)
{
    /// <summary>Nothing known: no series, no numbering, no movie identity.</summary>
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

        var work = await catalog.GetByIdAsync(workId, cancellationToken);
        if (work is null)
        {
            return EpisodeContext.None;
        }

        var imdbId = work.ExternalIds
            .FirstOrDefault(e => e.Provider == MetadataProvider.Imdb)?.Value;

        // A movie's unit is its own work (the movie path is UnitIds = [WorkId]): its identity is the work's.
        if (work.Kind != WorkKind.Series || unitIds.Count == 0 || unitIds.All(id => id == asset.Asset.WorkId))
        {
            return work.Kind == WorkKind.Series
                ? EpisodeContext.None
                : new EpisodeContext(null, null, null, null, work.Title, work.Year, imdbId);
        }

        var parentImdbId = imdbId;

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
