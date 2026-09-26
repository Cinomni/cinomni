namespace Cinomni.Search.Contracts;

/// <summary>Why a search was requested (recreates the RSS/missing/cutoff cadence).</summary>
public enum SearchReason
{
    Rss = 1,
    Missing = 2,
    Cutoff = 3,
}

/// <summary>
/// A neutral search criterion, decoupled from the Work. Monitoring resolves it from the catalog
/// (title/year/external ids) and hands it to Discovery in <see cref="SearchRequested"/>, so
/// Discovery federates indexers on a self-contained term without knowing the Work.
/// <para>
/// The episode-scoping members are <b>trailing optionals</b>: this record is serialized into the
/// persisted <c>SearchRequested</c> payload, so its existing members may never be reordered,
/// renamed or made required. They are deliberately plain scalars — this assembly references the
/// kernel only, so it must never name a Catalog type. <paramref name="Term"/> stays the series
/// title; the numbers ride alongside it.
/// </para>
/// </summary>
/// <param name="SeasonNumber">Season being searched for, or null for a movie/whole-series search.</param>
/// <param name="EpisodeNumber">Episode within <paramref name="SeasonNumber"/>, or null for a season pack.</param>
/// <param name="AbsoluteNumber">Absolute (anime) episode number, when the series is numbered that way.</param>
/// <param name="AirDate">Original air date, for date-based ("Show.2026.07.28") releases.</param>
/// <param name="TvdbId">TheTVDB series id — the id most TV-capable indexers key on.</param>
public sealed record SearchCriterion(
    string Term,
    int? Year,
    string? ImdbId,
    string? TmdbId,
    string ContentKind,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    int? AbsoluteNumber = null,
    DateOnly? AirDate = null,
    string? TvdbId = null);
