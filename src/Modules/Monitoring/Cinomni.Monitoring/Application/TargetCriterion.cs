using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;
using Cinomni.Search.Contracts;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// Builds the neutral <see cref="SearchCriterion"/> for one target. Shared by the scheduled sweep and
/// by the on-demand plan an interactive search asks for, because those two must ask an indexer the
/// very same question: a second, independently written criterion would drift and the reasons a user
/// reads for a manual search would stop describing the automatic one.
/// </summary>
internal static class TargetCriterion
{
    /// <summary>
    /// <c>Term</c> stays the <em>series</em> title — the numbers ride alongside it, because an indexer
    /// searches for "The Wire" with <c>season=2&amp;ep=5</c>, never for "The Wire S02E05" as a term. The
    /// TVDB id is included: it is the id most TV-capable indexers key on and the movie-era criterion
    /// extracted only IMDb and TMDB.
    /// </summary>
    public static SearchCriterion For(WorkSummary work, MonitoredTarget target) => new(
        Term: work.Title,
        Year: work.Year,
        ImdbId: ExternalValue(work, MetadataProvider.Imdb),
        TmdbId: ExternalValue(work, MetadataProvider.Tmdb),
        ContentKind: target.Kind.ToString(),
        SeasonNumber: target.SeasonNumber,
        EpisodeNumber: target.EpisodeNumber,
        AbsoluteNumber: target.AbsoluteNumber,
        // Only an episode has a meaningful air date to match a daily-show release against; a season's is
        // its premiere and would match the wrong thing.
        AirDate: target.Kind == TargetKind.Episode ? PublishedAirDateOf(target) : null,
        TvdbId: ExternalValue(work, MetadataProvider.Tvdb));

    /// <summary>What the target is called in a sentence a user reads ("Season 2", "S02E05").</summary>
    public static string LabelOf(MonitoredTarget target) => target.Kind switch
    {
        TargetKind.Season => target.SeasonNumber is 0 ? "Specials" : $"Season {target.SeasonNumber}",
        TargetKind.Episode => target.SeasonNumber is { } season && target.EpisodeNumber is { } episode
            ? $"S{season:D2}E{episode:D2}"
            : target.EpisodeTitle ?? "Episode",
        _ => "Movie",
    };

    /// <summary>
    /// The air date <em>as the provider published it</em> — that, not the gate
    /// instant, the key a date-numbered release ("Show.2026.07.28") is matched on. Deriving it from
    /// <see cref="MonitoredTarget.AirDate"/> would be a day late for every episode whose provider gave a
    /// tz-aware instant west of UTC, which is every US evening show sourced from TVMaze.
    /// <para>
    /// The fallback covers rows materialised before the published date was carried; for those the gate
    /// instant is all there is, and for a date-only provider the two agree anyway.
    /// </para>
    /// </summary>
    private static DateOnly? PublishedAirDateOf(MonitoredTarget target) =>
        target.PublishedAirDate
        ?? (target.AirDate is { } airsAt ? DateOnly.FromDateTime(airsAt.UtcDateTime) : null);

    private static string? ExternalValue(WorkSummary work, MetadataProvider provider) =>
        work.ExternalIds.FirstOrDefault(e => e.Provider == provider)?.Value;
}
