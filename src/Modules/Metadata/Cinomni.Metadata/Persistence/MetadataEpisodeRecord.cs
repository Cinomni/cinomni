using Cinomni.Kernel.Identifiers;
using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Persistence;

/// <summary>
/// One episode of a series snapshot, stored relationally as a cascade child of
/// <see cref="MetadataSnapshotRecord"/>. <c>(SnapshotId, SeasonNumber, Number)</c> is the
/// natural key; <see cref="AbsoluteNumber"/> is the anime ordering only TheTVDB publishes, so it stays
/// nullable and its index is filtered.
/// <para>
/// Two air-date fields on purpose. <see cref="AirDate"/> is the provider's published date exactly as
/// given and is what date-based release matching compares against. <see cref="AirDateTime"/> is set
/// <b>only</b> when the provider supplies a timezone-aware instant (TVMaze's <c>airstamp</c>) and is what
/// the unaired gate evaluates. Both are stored in UTC and are only ever taken from the provider — never
/// from the local clock.
/// </para>
/// </summary>
public sealed class MetadataEpisodeRecord
{
    private const int ShortText = 200;
    private const int TitleMax = 1000;
    private const int OverviewMax = 8000;
    private const int UrlMax = 1000;

    public Guid Id { get; init; }

    public Guid SnapshotId { get; init; }

    public int SeasonNumber { get; init; }

    public int Number { get; init; }

    /// <summary>The absolute (anime) episode number when the provider publishes one.</summary>
    public int? AbsoluteNumber { get; init; }

    public required string Title { get; init; }

    public string? Overview { get; init; }

    /// <summary>The published air date, date-only — the key date-based release matching compares against.</summary>
    public DateOnly? AirDate { get; init; }

    /// <summary>The timezone-aware air instant, when the provider gives one. Stored as UTC.</summary>
    public DateTimeOffset? AirDateTime { get; init; }

    public int? RuntimeMinutes { get; init; }

    /// <summary>The episode still frame the provider offers as its default.</summary>
    public string? StillUrl { get; init; }

    /// <summary>The provider's own episode id, kept for traceability only.</summary>
    public string? ExternalId { get; init; }

    /// <summary>Whether the provider classifies this as a special rather than a regular episode.</summary>
    public bool IsSpecial { get; init; }

    public static MetadataEpisodeRecord Create(Guid snapshotId, MetadataEpisode episode) => new()
    {
        Id = Uuid7.New(),
        SnapshotId = snapshotId,
        SeasonNumber = episode.SeasonNumber,
        Number = episode.Number,
        AbsoluteNumber = episode.AbsoluteNumber,
        Title = Text.Truncate(episode.Title, TitleMax)!,
        Overview = Text.Truncate(episode.Overview, OverviewMax),
        AirDate = episode.AirDate,
        AirDateTime = episode.AirDateTime?.ToUniversalTime(),
        RuntimeMinutes = episode.RuntimeMinutes,
        StillUrl = Text.Truncate(episode.StillUrl, UrlMax),
        ExternalId = Text.Truncate(episode.ExternalId, ShortText),
        IsSpecial = episode.IsSpecial,
    };

    public MetadataEpisode ToContract() => new(
        SeasonNumber,
        Number,
        Title,
        Overview,
        AbsoluteNumber,
        AirDate,
        AirDateTime,
        RuntimeMinutes,
        StillUrl,
        ExternalId,
        IsSpecial);
}
