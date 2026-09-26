using Cinomni.Kernel.Identifiers;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Persistence;

/// <summary>
/// One season of a series snapshot, stored relationally as a cascade child of
/// <see cref="MetadataSnapshotRecord"/> (a queryable set, not a jsonb blob) so Catalog can sync
/// its own hierarchy from it. <see cref="Number"/> is the natural key within a snapshot and season 0 is
/// the specials bucket. Every text value is clamped to its column width before persist, mirroring
/// <see cref="MetadataSnapshotRecord.Create"/> — one oversized provider string must not roll back a
/// 900-episode write.
/// </summary>
public sealed class MetadataSeasonRecord
{
    private const int ShortText = 200;
    private const int TitleMax = 500;
    private const int OverviewMax = 8000;
    private const int UrlMax = 1000;

    public Guid Id { get; init; }

    public Guid SnapshotId { get; init; }

    /// <summary>The season number as the provider orders it (0 = specials).</summary>
    public int Number { get; init; }

    public string? Title { get; init; }

    public string? Overview { get; init; }

    /// <summary>How many episodes the provider says the season holds, when it says so at all.</summary>
    public int? EpisodeCount { get; init; }

    /// <summary>The season premiere date as published, date-only (see <see cref="MetadataEpisodeRecord.AirDate"/>).</summary>
    public DateOnly? AirDate { get; init; }

    public string? PosterUrl { get; init; }

    /// <summary>The provider's own season id, kept for traceability only — Metadata never owns identity.</summary>
    public string? ExternalId { get; init; }

    public static MetadataSeasonRecord Create(Guid snapshotId, MetadataSeason season) => new()
    {
        Id = Uuid7.New(),
        SnapshotId = snapshotId,
        Number = season.Number,
        Title = Text.Truncate(season.Title, TitleMax),
        Overview = Text.Truncate(season.Overview, OverviewMax),
        EpisodeCount = season.EpisodeCount,
        AirDate = season.AirDate,
        PosterUrl = Text.Truncate(season.PosterUrl, UrlMax),
        ExternalId = Text.Truncate(season.ExternalId, ShortText),
    };

    public MetadataSeason ToContract() =>
        new(Number, Title, Overview, EpisodeCount, AirDate, PosterUrl, ExternalId);
}
