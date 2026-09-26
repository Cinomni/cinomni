using Cinomni.Kernel.Identifiers;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Persistence;

/// <summary>
/// The stored neutral snapshot of a work's metadata fetched from a provider. The
/// neutral fields are columns (queryable); the untouched provider response is kept as a controlled JSONB
/// blob for debugging and re-mapping. References the work by id (inter-schema, no physical FK)
/// — Metadata does not own identity. <see cref="PosterUrl"/>/<see cref="BackdropUrl"/> hold the currently
/// selected artwork; the full candidate set hangs off <see cref="Artwork"/>. Projected to the public
/// <c>MetadataSnapshot</c> contract on read.
/// </summary>
public sealed class MetadataSnapshotRecord
{
    private const int ShortText = 200;
    private const int TitleMax = 1000;
    private const int UrlMax = 1000;
    private const int OverviewMax = 8000;

    /// <summary>A work has a handful of genres; a provider answering with hundreds is not describing it.</summary>
    private const int MaxGenres = 32;

    public Guid Id { get; init; }

    public Guid WorkId { get; init; }

    public required string Provider { get; init; }

    public MetadataMediaKind Kind { get; init; }

    public required string ExternalId { get; init; }

    public required string Title { get; init; }

    public string? OriginalTitle { get; init; }

    public int? Year { get; init; }

    public string? Overview { get; init; }

    public int? RuntimeMinutes { get; init; }

    public string? OriginalLanguage { get; init; }

    /// <summary>The currently selected poster (mutable — a manual override re-points it).</summary>
    public string? PosterUrl { get; set; }

    /// <summary>The currently selected backdrop (mutable — a manual override re-points it).</summary>
    public string? BackdropUrl { get; set; }

    /// <summary>The production status of a series (<c>null</c> for a movie). Drives the refresh cadence.</summary>
    public SeriesStatus? SeriesStatus { get; init; }

    /// <summary>The date the series first aired, as published (date-only).</summary>
    public DateOnly? FirstAired { get; init; }

    /// <summary>The date the series last aired, as published (date-only).</summary>
    public DateOnly? LastAired { get; init; }

    /// <summary>The TheTVDB id this snapshot cross-references, when the provider publishes one.</summary>
    public string? TvdbId { get; init; }

    /// <summary>The IMDb id this snapshot cross-references, when the provider publishes one.</summary>
    public string? ImdbId { get; init; }

    /// <summary>The TMDB id this snapshot cross-references, when the provider publishes one.</summary>
    public string? TmdbId { get; init; }

    /// <summary>
    /// Which season ordering produced the numbers below (e.g. <c>official</c>, <c>dvd</c>,
    /// <c>absolute</c>). Recorded because the choice decides which SxxEyy numbers reach the catalog, and
    /// a later snapshot taken under a different ordering must be recognisable as such.
    /// </summary>
    public string? SeasonOrder { get; init; }

    /// <summary>
    /// The genre names the provider published, in its order. Stored as an array rather than a joined
    /// table because nothing owns a genre here — it is a label this provider used, not an entity, and
    /// a table would invite reconciling two providers' vocabularies as though that had one answer.
    /// </summary>
    public List<string> Genres { get; init; } = [];

    /// <summary>
    /// The age classification for the region this installation is configured for, or null when it has
    /// named none or the provider publishes none for it. Null is ordinary and not a failure.
    /// </summary>
    public string? ContentRating { get; init; }

    /// <summary>The untouched provider response, kept as a controlled JSONB blob.</summary>
    public required string RawResponse { get; init; }

    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>Every artwork candidate the provider offered (relational children).</summary>
    public List<MetadataArtworkRecord> Artwork { get; } = [];

    /// <summary>The seasons of a series snapshot (empty for a movie). Cascade children, read separately.</summary>
    public List<MetadataSeasonRecord> Seasons { get; } = [];

    /// <summary>The episodes of a series snapshot (empty for a movie). Cascade children, read separately.</summary>
    public List<MetadataEpisodeRecord> Episodes { get; } = [];

    public static MetadataSnapshotRecord Create(
        Guid workId,
        string provider,
        MetadataMediaKind kind,
        string externalId,
        string title,
        string? originalTitle,
        int? year,
        string? overview,
        int? runtimeMinutes,
        string? originalLanguage,
        string? posterUrl,
        string? backdropUrl,
        string rawResponse,
        DateTimeOffset now,
        SeriesSnapshotDetails? series = null,
        IReadOnlyList<string>? genres = null,
        string? contentRating = null) => new()
    {
        Id = Uuid7.New(),
        WorkId = workId,
        Provider = Text.Truncate(provider, ShortText)!,
        Kind = kind,
        ExternalId = Text.Truncate(externalId, ShortText)!,
        Title = Text.Truncate(title, TitleMax)!,
        OriginalTitle = Text.Truncate(originalTitle, TitleMax),
        Year = year,
        Overview = Text.Truncate(overview, OverviewMax),
        RuntimeMinutes = runtimeMinutes,
        OriginalLanguage = Text.Truncate(originalLanguage, ShortText),
        PosterUrl = Text.Truncate(posterUrl, UrlMax),
        BackdropUrl = Text.Truncate(backdropUrl, UrlMax),
        SeriesStatus = series?.Status,
        FirstAired = series?.FirstAired,
        LastAired = series?.LastAired,
        TvdbId = Text.Truncate(series?.TvdbId, ShortText),
        ImdbId = Text.Truncate(series?.ImdbId, ShortText),
        TmdbId = Text.Truncate(series?.TmdbId, ShortText),
        SeasonOrder = Text.Truncate(series?.SeasonOrder, ShortText),
        // Clamped and bounded at the anti-corruption layer like every other provider string: a genre
        // list is untrusted input, and neither its length nor an entry's is ours to assume.
        Genres = [.. (genres ?? [])
            .Select(genre => Text.Truncate(genre, ShortText))
            .OfType<string>()
            .Where(genre => genre.Length > 0)
            .Take(MaxGenres)],
        ContentRating = Text.Truncate(contentRating, ShortText),
        RawResponse = rawResponse,
        FetchedAt = now,
    };
}

/// <summary>
/// The series-only attributes of a snapshot, grouped so <see cref="MetadataSnapshotRecord.Create"/> takes
/// one trailing optional argument instead of seven. A movie refresh simply passes nothing.
/// </summary>
public sealed record SeriesSnapshotDetails(
    SeriesStatus? Status = null,
    DateOnly? FirstAired = null,
    DateOnly? LastAired = null,
    string? TvdbId = null,
    string? ImdbId = null,
    string? TmdbId = null,
    string? SeasonOrder = null);
