using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;

namespace Cinomni.Metadata.Contracts;

/// <summary>
/// The failures the Metadata module answers a caller with. Stable codes: the client branches on them and
/// the HTTP surface maps them to a status.
/// </summary>
public static class MetadataErrors
{
    /// <summary>
    /// No provider covering the requested kind is configured, so the search was never put to anybody.
    /// This is deliberately <b>not</b> an empty result: an empty result means every provider was asked
    /// and none had a match, and a caller that cannot tell the two apart ends up telling a user that a
    /// film does not exist when the truth is that nothing was asked.
    /// </summary>
    public const string NoProvider = "metadata.no_provider";

    /// <summary>
    /// The unanswered-question failure, phrased for whoever is looking at the screen. It names the
    /// providers that could have answered and what they lack — an API key is the only thing that makes
    /// one unavailable — so an administrator knows what to fix without reading the log. The configuration
    /// key itself belongs in the log (the provider names it there, once).
    /// </summary>
    /// <param name="kind">The kind that was searched for.</param>
    /// <param name="unconfigured">Source names (<c>tmdb</c>, <c>tvdb</c>…) of the providers that cover
    /// <paramref name="kind"/> but have no API key; empty when no provider covers it at all.</param>
    public static Error NoProviderAvailable(MetadataMediaKind kind, IReadOnlyCollection<string> unconfigured)
    {
        var what = Describe(kind);
        if (unconfigured.Count == 0)
        {
            return new Error(
                NoProvider,
                $"No metadata provider is enabled for {what}, so this search could not be answered. "
                + "An administrator has to enable one.");
        }

        var names = string.Join(" or ", unconfigured.Select(DisplayName));
        return new Error(
            NoProvider,
            $"Searching for {what} needs {names}, and no API key is configured for "
            + $"{(unconfigured.Count == 1 ? "it" : "them")}. An administrator can add one under Settings.");
    }

    private static string Describe(MetadataMediaKind kind) =>
        kind == MetadataMediaKind.Series ? "series" : "movies";

    private static string DisplayName(string source) => source.ToLowerInvariant() switch
    {
        "tmdb" => "TMDB",
        "tvdb" => "TheTVDB",
        "tvmaze" => "TVmaze",
        _ => source,
    };
}

/// <summary>Stable internal identity of a metadata snapshot (UUIDv7).</summary>
public readonly record struct MetadataSnapshotId(Guid Value)
{
    public static MetadataSnapshotId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The kind of work a metadata lookup targets. Providers declare which kinds they cover, so a movie
/// search never queries a TV-only provider (and vice versa). The movie slice uses <see cref="Movie"/>;
/// <see cref="Series"/> is wired for the series slice.
/// </summary>
public enum MetadataMediaKind
{
    Movie = 1,
    Series = 2,
}

/// <summary>
/// The role a piece of artwork plays. Selection is per kind (one poster, one backdrop, …) and — for a
/// series — per scope, so a season poster never competes with the series poster. Persisted as text via
/// <c>HasConversion&lt;string&gt;()</c>, so members may only be <b>appended</b>: renaming or renumbering
/// an existing member silently reinterprets stored rows.
/// </summary>
public enum ArtworkKind
{
    Poster = 1,
    Backdrop = 2,
    Logo = 3,

    /// <summary>An episode still frame (series only — scoped by season and episode number).</summary>
    Still = 4,
}

/// <summary>
/// The production status of a series as the provider reports it. Drives the refresh cadence (a
/// <see cref="Continuing"/> series is polled far more often than an <see cref="Ended"/> one) and the
/// catalog work status. Persisted as text, so members may only be <b>appended</b>.
/// </summary>
public enum SeriesStatus
{
    /// <summary>The provider gave no status, or one we do not map.</summary>
    Unknown = 1,

    /// <summary>Still airing or between seasons — new episodes are expected.</summary>
    Continuing = 2,

    /// <summary>Concluded — the episode list is final.</summary>
    Ended = 3,

    /// <summary>Announced but not yet aired.</summary>
    Upcoming = 4,

    /// <summary>Cancelled before concluding — treated as final, like <see cref="Ended"/>.</summary>
    Cancelled = 5,
}

/// <summary>
/// A single artwork candidate of a snapshot (poster/backdrop/logo/still) with the attributes the
/// selection policy ranks on: language, dimensions and the provider's community vote.
/// <see cref="IsSelected"/> marks the one currently applied for its <see cref="Kind"/> <i>within its
/// scope</i>: <see cref="SeasonNumber"/> and <see cref="EpisodeNumber"/> are both <c>null</c> for
/// series-level (or movie) artwork, season-scoped artwork sets only the season, and an episode still
/// sets both.
/// </summary>
public sealed record MetadataArtwork(
    Guid Id,
    ArtworkKind Kind,
    string Url,
    string? Language,
    int? Width,
    int? Height,
    double? VoteAverage,
    int? VoteCount,
    bool IsSelected,
    int? SeasonNumber = null,
    int? EpisodeNumber = null);

/// <summary>
/// One season of a series as a provider describes it. <see cref="Number"/> is the natural key within a
/// snapshot (season 0 is the specials bucket); <see cref="ExternalId"/> is the provider's own id, kept
/// only for traceability — Metadata never carries identity.
/// </summary>
public sealed record MetadataSeason(
    int Number,
    string? Title,
    string? Overview,
    int? EpisodeCount,
    DateOnly? AirDate,
    string? PosterUrl,
    string? ExternalId);

/// <summary>
/// One episode of a series as a provider describes it. <c>(SeasonNumber, Number)</c> is the natural key
/// within a snapshot.
/// <para>
/// Air dates follow the slice's two-field rule: <see cref="AirDate"/> is the provider's published date
/// exactly as given and is the key that date-based release matching (<c>Show.2026.07.28</c>) compares
/// against; <see cref="AirDateTime"/> is populated <b>only</b> when the provider supplies a timezone-aware
/// instant (TVMaze's <c>airstamp</c>) and is what the unaired gate and the Future/Existing monitoring
/// modes evaluate, falling back to <c>AirDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)</c>.
/// Everything stored is UTC.
/// </para>
/// </summary>
public sealed record MetadataEpisode(
    int SeasonNumber,
    int Number,
    string Title,
    string? Overview,
    int? AbsoluteNumber,
    DateOnly? AirDate,
    DateTimeOffset? AirDateTime,
    int? RuntimeMinutes,
    string? StillUrl,
    string? ExternalId,
    bool IsSpecial);

/// <summary>
/// The season/episode tree of one snapshot, read through <c>IMetadataQuery.GetSeriesStructureAsync</c>
/// as a <b>separate</b> query — it is deliberately not part of <c>MetadataSnapshot</c>, because adding
/// seasons and episodes to the snapshot's artwork <c>Include</c> chain would produce an
/// artwork × episodes cartesian product on every movie detail page. <see cref="Seasons"/> is ordered by
/// number and <see cref="Episodes"/> by season then episode number.
/// </summary>
public sealed record MetadataSeriesStructure(
    MetadataSnapshotId SnapshotId,
    IReadOnlyList<MetadataSeason> Seasons,
    IReadOnlyList<MetadataEpisode> Episodes);

/// <summary>
/// A neutral, provider-agnostic snapshot of a work's metadata. Catalog copies these fields
/// onto its own <c>Work</c> — the snapshot never carries identity; the external provider id is just one
/// of its attributes. Multi-provider from the start: <see cref="Provider"/> records the origin, and
/// <see cref="Kind"/> the media kind. <see cref="PosterUrl"/>/<see cref="BackdropUrl"/> are the currently
/// selected artwork; <see cref="Artwork"/> carries every candidate the provider offered.
/// <para>
/// The series members are <b>trailing and optional</b> and stay that way: this record is consumed by
/// Catalog's <c>AttachMetadataSnapshotCommandHandler</c>, by <c>MetadataQuery</c> and by
/// <c>MetadataEndpoints.ToSnapshotDto</c>, so reordering or inserting a member is a cross-module compile
/// break under <c>TreatWarningsAsErrors</c>. The season/episode tree is deliberately absent — read it
/// through <c>IMetadataQuery.GetSeriesStructureAsync</c>.
/// </para>
/// </summary>
public sealed record MetadataSnapshot(
    MetadataSnapshotId Id,
    Guid WorkId,
    string Provider,
    MetadataMediaKind Kind,
    string ExternalId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    int? RuntimeMinutes,
    string? OriginalLanguage,
    string? PosterUrl,
    string? BackdropUrl,
    DateTimeOffset FetchedAt,
    IReadOnlyList<MetadataArtwork> Artwork,
    SeriesStatus? SeriesStatus = null,
    DateOnly? FirstAired = null,
    DateOnly? LastAired = null,
    string? TvdbId = null,
    string? ImdbId = null,
    string? TmdbId = null,
    string? SeasonOrder = null,
    /// <summary>The provider's genre names, in its order. Null or empty when it published none.</summary>
    IReadOnlyList<string>? Genres = null,
    /// <summary>
    /// The classification for the region this installation named, or null — because it named none,
    /// because the provider publishes none for it, or because the work is genuinely unrated. All
    /// three are the same answer to a consumer: nothing is known, so nothing may be assumed.
    /// </summary>
    string? ContentRating = null);

/// <summary>
/// A search hit from a provider: enough to pick a work before fetching its full snapshot. The trailing
/// external ids let a series search be de-duplicated across providers — the same show otherwise comes
/// back once per provider with no way to tell the rows apart.
/// </summary>
public sealed record MetadataCandidate(
    string Provider,
    MetadataMediaKind Kind,
    string ExternalId,
    string Title,
    int? Year,
    string? Overview,
    string? TvdbId = null,
    string? ImdbId = null,
    string? TmdbId = null);
