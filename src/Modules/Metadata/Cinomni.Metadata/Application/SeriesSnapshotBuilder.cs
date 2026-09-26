using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;

namespace Cinomni.Metadata.Application;

/// <summary>
/// Maps a provider's series result onto the stored snapshot: its scalar series attributes, its scoped
/// artwork candidates, and its season/episode children. Pure and side-effect free — the children are
/// attached to the (still unsaved) snapshot so the existing <c>Snapshots.Add</c> cascade persists them
/// inside the same unit of work as <c>MetadataRefreshed</c>.
/// <para>
/// A provider that lists the same season or the same <c>(season, episode)</c> twice is de-duplicated
/// here, first occurrence winning. That is not defensive padding: those pairs are unique indexes, and one
/// duplicated row would roll back the entire refresh — including the event — rather than lose one episode.
/// </para>
/// </summary>
internal static class SeriesSnapshotBuilder
{
    /// <summary>The snapshot's series-only scalars, or <c>null</c> for a movie result.</summary>
    public static SeriesSnapshotDetails? DetailsOf(ProviderMetadataResult result) =>
        result.Series is not { } series
            ? null
            : new SeriesSnapshotDetails(
                series.Status,
                series.FirstAired,
                series.LastAired,
                series.ExternalIds.TvdbId,
                series.ExternalIds.ImdbId,
                series.ExternalIds.TmdbId,
                series.SeasonOrder);

    /// <summary>
    /// Every artwork candidate of the result, each carrying its scope: the provider's own set is
    /// series-level, a season's poster is season-scoped, and an episode's still is episode-scoped. The
    /// selection policy then runs once per scope, so a season poster never competes with the series one.
    /// </summary>
    public static IReadOnlyList<ProviderArtwork> ArtworkCandidates(ProviderMetadataResult result)
    {
        if (result.Series is not { } series)
        {
            return result.Artwork;
        }

        var candidates = new List<ProviderArtwork>(result.Artwork);
        foreach (var season in series.Seasons)
        {
            if (!string.IsNullOrEmpty(season.PosterUrl))
            {
                candidates.Add(new ProviderArtwork(
                    ArtworkKind.Poster, season.PosterUrl, Language: null, Width: null, Height: null,
                    VoteAverage: null, VoteCount: null, SeasonNumber: season.Number));
            }
        }

        foreach (var episode in series.Episodes)
        {
            if (!string.IsNullOrEmpty(episode.StillUrl))
            {
                candidates.Add(new ProviderArtwork(
                    ArtworkKind.Still, episode.StillUrl, Language: null, Width: null, Height: null,
                    VoteAverage: null, VoteCount: null, SeasonNumber: episode.SeasonNumber, EpisodeNumber: episode.Number));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Attaches the season and episode children to <paramref name="snapshot"/> before it is added, so EF
    /// cascades them in the same <c>SaveChanges</c> that writes the snapshot and the outbox row.
    /// </summary>
    public static void AttachStructure(MetadataSnapshotRecord snapshot, ProviderSeriesDetails series)
    {
        var seenSeasons = new HashSet<int>();
        foreach (var season in series.Seasons)
        {
            if (seenSeasons.Add(season.Number))
            {
                snapshot.Seasons.Add(MetadataSeasonRecord.Create(snapshot.Id, ToContract(season)));
            }
        }

        var seenEpisodes = new HashSet<(int Season, int Number)>();
        foreach (var episode in series.Episodes)
        {
            if (seenEpisodes.Add((episode.SeasonNumber, episode.Number)))
            {
                snapshot.Episodes.Add(MetadataEpisodeRecord.Create(snapshot.Id, ToContract(episode)));
            }
        }
    }

    private static MetadataSeason ToContract(ProviderSeason season) => new(
        season.Number,
        season.Title,
        season.Overview,
        season.EpisodeCount,
        season.AirDate,
        season.PosterUrl,
        season.ExternalId);

    private static MetadataEpisode ToContract(ProviderEpisode episode) => new(
        episode.SeasonNumber,
        episode.Number,
        episode.Title,
        episode.Overview,
        episode.AbsoluteNumber,
        episode.AirDate,
        episode.AirDateTime,
        episode.RuntimeMinutes,
        episode.StillUrl,
        episode.ExternalId,
        episode.IsSpecial);
}
