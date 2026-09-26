using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The single implementation of "resolve a release's numbering to the concrete catalog units it covers".
/// Monitoring materialises targets through it, Decision computes pack coverage with it and Import maps a
/// file to an episode with it — so no module ever grows numbering logic of its own.
/// <para>
/// Every resolver is one <c>AsNoTracking</c> query aimed at the index built for it:
/// <c>ux_episodes_work_season_number</c> for <c>SxxEyy</c> and ranges,
/// <c>ux_episodes_work_absolute_number</c> (filtered) for anime, and <c>ix_episodes_work_air_date</c> for
/// daily shows. An unknown work, season or episode yields an empty list, never an exception: resolution
/// runs on hostile release titles and a miss is an ordinary outcome.
/// </para>
/// </summary>
public sealed class CatalogSeriesQuery(CatalogDbContext dbContext) : ICatalogSeriesQuery
{
    public Task<IReadOnlyList<EpisodeSummary>> ResolveEpisodeAsync(
        WorkId workId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken = default) =>
        ListAsync(
            Episodes(workId).Where(e => e.SeasonNumber == seasonNumber && e.Number == episodeNumber),
            cancellationToken);

    public Task<IReadOnlyList<EpisodeSummary>> ResolveEpisodeRangeAsync(
        WorkId workId,
        int seasonNumber,
        int firstEpisodeNumber,
        int lastEpisodeNumber,
        CancellationToken cancellationToken = default)
    {
        // Tolerate a reversed range (E05-E03): a parser fed a hostile title should not silently resolve
        // to nothing when the intent is unambiguous.
        var first = Math.Min(firstEpisodeNumber, lastEpisodeNumber);
        var last = Math.Max(firstEpisodeNumber, lastEpisodeNumber);

        return ListAsync(
            Episodes(workId).Where(e => e.SeasonNumber == seasonNumber && e.Number >= first && e.Number <= last),
            cancellationToken);
    }

    public Task<IReadOnlyList<EpisodeSummary>> ResolveSeasonAsync(
        WorkId workId,
        int seasonNumber,
        CancellationToken cancellationToken = default) =>
        ListAsync(Episodes(workId).Where(e => e.SeasonNumber == seasonNumber), cancellationToken);

    public Task<IReadOnlyList<EpisodeSummary>> ResolveByAbsoluteNumberAsync(
        WorkId workId,
        int absoluteNumber,
        CancellationToken cancellationToken = default) =>
        ListAsync(Episodes(workId).Where(e => e.AbsoluteNumber == absoluteNumber), cancellationToken);

    public Task<IReadOnlyList<EpisodeSummary>> ResolveByAirDateAsync(
        WorkId workId,
        DateOnly airDate,
        CancellationToken cancellationToken = default) =>
        ListAsync(Episodes(workId).Where(e => e.AirDate == airDate), cancellationToken);

    public async Task<IReadOnlyList<SeasonSummary>> GetSeasonsAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        var seasons = await dbContext.Seasons
            .AsNoTracking()
            .Where(s => s.WorkId == workId.Value)
            .OrderBy(s => s.Number)
            .ToListAsync(cancellationToken);

        return seasons.Select(ToSummary).ToList();
    }

    public Task<IReadOnlyList<EpisodeSummary>> GetEpisodesAsync(
        WorkId workId,
        int seasonNumber,
        CancellationToken cancellationToken = default) =>
        ListAsync(Episodes(workId).Where(e => e.SeasonNumber == seasonNumber), cancellationToken);

    public Task<IReadOnlyList<EpisodeSummary>> GetAllEpisodesAsync(
        WorkId workId,
        CancellationToken cancellationToken = default) =>
        ListAsync(Episodes(workId), cancellationToken);

    /// <summary>
    /// One episode of a work by id, for the detail route and for the modules that arrive holding a
    /// catalog unit id rather than numbering (Subtitles resolving <c>SxxEyy</c> for a provider query,
    /// Playback resolving "next up").
    /// </summary>
    public async Task<EpisodeSummary?> GetEpisodeAsync(
        WorkId workId,
        EpisodeId episodeId,
        CancellationToken cancellationToken = default)
    {
        var episode = await Episodes(workId)
            .FirstOrDefaultAsync(e => e.Id == episodeId.Value, cancellationToken);

        return episode is null ? null : ToSummary(episode);
    }

    private IQueryable<Episode> Episodes(WorkId workId) =>
        dbContext.Episodes.AsNoTracking().Where(e => e.WorkId == workId.Value);

    private static async Task<IReadOnlyList<EpisodeSummary>> ListAsync(
        IQueryable<Episode> query,
        CancellationToken cancellationToken)
    {
        var episodes = await query
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.Number)
            .ToListAsync(cancellationToken);

        return episodes.Select(ToSummary).ToList();
    }

    private static SeasonSummary ToSummary(Season season) => new(
        new SeasonId(season.Id),
        new WorkId(season.WorkId),
        season.Number,
        season.Title,
        season.AirDate,
        season.ExpectedEpisodeCount,
        season.PosterUrl);

    private static EpisodeSummary ToSummary(Episode episode) => new(
        new EpisodeId(episode.Id),
        new SeasonId(episode.SeasonId),
        new WorkId(episode.WorkId),
        episode.SeasonNumber,
        episode.Number,
        episode.AbsoluteNumber,
        episode.Title,
        episode.AirDate,
        episode.AirDateTime,
        episode.RuntimeMinutes,
        episode.HasAsset);
}
