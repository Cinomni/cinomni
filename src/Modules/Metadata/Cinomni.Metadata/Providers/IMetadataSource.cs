using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Providers;

/// <summary>What to search a provider for: the term, an optional release year, and the media kind.</summary>
public sealed record MetadataProviderQuery(string Term, int? Year, MetadataMediaKind Kind);

/// <summary>
/// The season orderings a provider can publish. TheTVDB exposes all three; TMDB and TVMaze publish only
/// their own (which is the official one). Whichever a refresh used becomes the SxxEyy numbering the rest
/// of the platform matches releases against, so it is recorded on the snapshot rather than left implicit.
/// </summary>
public static class SeasonOrders
{
    /// <summary>The broadcast/official ordering — the default everywhere.</summary>
    public const string Official = "official";

    /// <summary>The DVD ordering, where a provider publishes one.</summary>
    public const string Dvd = "dvd";

    /// <summary>The flat absolute ordering used by anime.</summary>
    public const string Absolute = "absolute";

    /// <summary>Whether <paramref name="value"/> is one of the orderings we know how to request.</summary>
    public static bool IsKnown(string? value) =>
        value is not null
        && (value.Equals(Official, StringComparison.OrdinalIgnoreCase)
            || value.Equals(Dvd, StringComparison.OrdinalIgnoreCase)
            || value.Equals(Absolute, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The external ids a provider cross-references on a record. They are what lets the same show found
/// through three providers be recognised as one row instead of three: a TVMaze hit carries TheTVDB's id,
/// a TheTVDB hit carries IMDb's and TMDB's. Every member is optional — a provider that publishes none
/// simply yields <see cref="None"/>.
/// </summary>
public sealed record ProviderExternalIds(string? TvdbId = null, string? ImdbId = null, string? TmdbId = null)
{
    /// <summary>No cross-references at all.</summary>
    public static readonly ProviderExternalIds None = new();

    public bool IsEmpty => TvdbId is null && ImdbId is null && TmdbId is null;
}

/// <summary>
/// A search hit from a provider, with its opaque external id. <see cref="ExternalIds"/> is trailing and
/// optional so a provider that publishes no cross-references (and every movie adapter that predates the
/// series slice) constructs the record exactly as before.
/// </summary>
public sealed record ProviderMetadataCandidate(
    string ExternalId,
    string Title,
    int? Year,
    string? Overview,
    ProviderExternalIds? ExternalIds = null);

/// <summary>
/// One piece of artwork a provider offers for a work, with the attributes the selection policy ranks on.
/// <see cref="Language"/> is a BCP-47 / ISO-639 tag (or <c>null</c> for a text-less image).
/// <para>
/// <see cref="SeasonNumber"/>/<see cref="EpisodeNumber"/> are the candidate's <i>scope</i>: both
/// <c>null</c> for series-level (and movie) artwork, season only for a season poster, both for an episode
/// still. Selection runs once per scope, so a season poster can never win the series poster slot.
/// </para>
/// </summary>
public sealed record ProviderArtwork(
    ArtworkKind Kind,
    string Url,
    string? Language,
    int? Width,
    int? Height,
    double? VoteAverage,
    int? VoteCount,
    int? SeasonNumber = null,
    int? EpisodeNumber = null);

/// <summary>
/// One season of a series as a provider describes it. The port keeps its own vocabulary rather than
/// speaking <c>MetadataSeason</c> — that is the anti-corruption layer: an adapter maps its provider's
/// shape to this, and only the domain maps this to the contract.
/// </summary>
public sealed record ProviderSeason(
    int Number,
    string? Title,
    string? Overview,
    int? EpisodeCount,
    DateOnly? AirDate,
    string? PosterUrl,
    string? ExternalId);

/// <summary>
/// One episode of a series as a provider describes it. <c>(SeasonNumber, Number)</c> is the natural key.
/// <para>
/// Two air-date fields on purpose. <see cref="AirDate"/> is the provider's published
/// date exactly as given and is always populated when the provider has one — it is the key date-based
/// release matching compares against. <see cref="AirDateTime"/> is populated <b>only</b> from a genuinely
/// timezone-aware provider field (TVMaze's <c>airstamp</c>); TheTVDB's <c>aired</c> and TMDB's
/// <c>air_date</c> are date-only, so those adapters leave it <c>null</c> rather than inventing midnight UTC.
/// </para>
/// </summary>
public sealed record ProviderEpisode(
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
/// The series-only part of a provider result, grouped into one member so
/// <see cref="ProviderMetadataResult"/> stays an 11-member record instead of growing to eighteen across
/// four construction sites. A movie result simply carries <c>null</c>.
/// </summary>
public sealed record ProviderSeriesDetails(
    SeriesStatus Status,
    DateOnly? FirstAired,
    DateOnly? LastAired,
    string SeasonOrder,
    ProviderExternalIds ExternalIds,
    IReadOnlyList<ProviderSeason> Seasons,
    IReadOnlyList<ProviderEpisode> Episodes);

/// <summary>
/// A full neutral snapshot fetched from a provider, plus the untouched raw response. <see cref="Artwork"/>
/// is the full candidate set; <see cref="PosterUrl"/>/<see cref="BackdropUrl"/> are the provider's own
/// defaults (used as fallback candidates when the artwork list is empty).
/// <para>
/// <see cref="RawJson"/> must stay small: it lands in a jsonb column and a 900-episode embed is megabytes
/// per refresh, so an adapter strips the episode array out of the raw payload it returns.
/// <see cref="Series"/> is trailing and optional — a movie result is constructed exactly as before.
/// </para>
/// </summary>
/// <param name="Genres">
/// The provider's genre names, as it publishes them and in its order. Not normalised to a closed
/// vocabulary: providers disagree about what the set even is, and flattening theirs into ours would
/// throw away the distinction rather than resolve it. What consumes them decides what to do with
/// "Sci-Fi &amp; Fantasy" meeting "Science Fiction".
/// </param>
/// <param name="ContentRating">
/// The age classification for the region this installation is configured for, or null when the
/// provider publishes none for it. Deliberately one string and not a set: classifications are
/// regional and an installation watches in one place, so keeping every region a provider offers
/// would be storing an answer to a question nobody asked.
/// <para>
/// Null is common and is not a failure. A provider that has no certification for the configured
/// region says nothing, and a work with no rating has to stay watchable — a parental control that
/// blocks the unrated by default would hide most of a library on the day it is turned on.
/// </para>
/// </param>
public sealed record ProviderMetadataResult(
    string ExternalId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    int? RuntimeMinutes,
    string? OriginalLanguage,
    string? PosterUrl,
    string? BackdropUrl,
    string RawJson,
    IReadOnlyList<ProviderArtwork> Artwork,
    ProviderSeriesDetails? Series = null,
    // Two plain members rather than one grouping record: series details are grouped because they are
    // a cluster that only applies to series, and these two apply to everything and are not one idea.
    // Genre is descriptive, a classification is regulatory, and pairing them to save a slot would
    // suggest a relationship that is not there.
    IReadOnlyList<string>? Genres = null,
    string? ContentRating = null);

/// <summary>
/// Port to an external metadata provider. The production adapter fetches over an
/// SSRF-hardened client and treats the response as untrusted; tests substitute a fake. Behind this port
/// the domain never sees a provider's types — the anti-corruption layer. A provider declares which media
/// kinds it covers via <see cref="SupportedKinds"/>, so orchestration only routes it work it understands.
/// </summary>
public interface IMetadataSource
{
    string Name { get; }

    /// <summary>The media kinds this provider can answer for (e.g. TMDB movies, TVMaze series).</summary>
    IReadOnlySet<MetadataMediaKind> SupportedKinds { get; }

    /// <summary>
    /// Whether this provider is configured well enough to answer at all. A provider that needs a
    /// credential and has none is <c>false</c>: it can neither match nor miss, so a search left with
    /// only unavailable providers is an <i>unanswered question</i> and not an empty result — the
    /// distinction the caller needs to avoid telling a user that a film does not exist. Defaults to
    /// <c>true</c>, which is right for a provider that needs no configuration at all (TVMaze).
    /// </summary>
    bool IsAvailable => true;

    /// <summary>
    /// Says, once for the process, that this provider is switched off and names the configuration key
    /// that would switch it on. Orchestration calls it on the providers it <i>skips</i>, because a
    /// provider that is never called can never explain itself — and an installation that quietly asks
    /// nobody is the failure this exists to prevent. A no-op when the provider is available.
    /// </summary>
    void AnnounceUnavailable()
    {
    }

    Task<IReadOnlyList<ProviderMetadataCandidate>> SearchAsync(MetadataProviderQuery query, CancellationToken cancellationToken = default);

    Task<ProviderMetadataResult?> FetchAsync(string externalId, MetadataMediaKind kind, CancellationToken cancellationToken = default);
}
