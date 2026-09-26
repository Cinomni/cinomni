using Cinomni.Kernel.Identifiers;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;

namespace Cinomni.Metadata.Persistence;

/// <summary>
/// One artwork candidate of a snapshot (poster/backdrop/logo), stored relationally as a child of
/// <see cref="MetadataSnapshotRecord"/> (a queryable set, not a jsonb blob). Persisting every
/// candidate is what lets an operator re-pick later without re-fetching the provider. <see cref="IsSelected"/>
/// is mutable: the automatic policy sets it, and a manual override flips it.
/// </summary>
public sealed class MetadataArtworkRecord
{
    private const int UrlMax = 1000;
    private const int LanguageMax = 20;

    public Guid Id { get; init; }

    public Guid SnapshotId { get; init; }

    public ArtworkKind Kind { get; init; }

    public required string Url { get; init; }

    public string? Language { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? VoteAverage { get; init; }

    public int? VoteCount { get; init; }

    /// <summary>Whether this candidate is the one currently applied for its kind (mutable — override flips it).</summary>
    public bool IsSelected { get; set; }

    /// <summary>Provider order, preserved so the UI can list candidates as the provider ranked them.</summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// The season this candidate belongs to, or <c>null</c> for series-level (and movie) artwork. Together
    /// with <see cref="EpisodeNumber"/> this is the <i>scope</i> of the selection: exactly one candidate is
    /// selected per (kind, season, episode), so a season poster can never win the series poster slot.
    /// </summary>
    public int? SeasonNumber { get; init; }

    /// <summary>The episode this candidate belongs to (an episode still), or <c>null</c> when not episode-scoped.</summary>
    public int? EpisodeNumber { get; init; }

    public static MetadataArtworkRecord Create(
        Guid snapshotId,
        ProviderArtwork artwork,
        bool isSelected,
        int ordinal,
        int? seasonNumber = null,
        int? episodeNumber = null) => new()
    {
        Id = Uuid7.New(),
        SnapshotId = snapshotId,
        Kind = artwork.Kind,
        Url = Text.Truncate(artwork.Url, UrlMax)!,
        Language = Text.Truncate(artwork.Language, LanguageMax),
        Width = artwork.Width,
        Height = artwork.Height,
        VoteAverage = artwork.VoteAverage,
        VoteCount = artwork.VoteCount,
        IsSelected = isSelected,
        Ordinal = ordinal,
        SeasonNumber = seasonNumber,
        EpisodeNumber = episodeNumber,
    };

    public MetadataArtwork ToContract() =>
        new(Id, Kind, Url, Language, Width, Height, VoteAverage, VoteCount, IsSelected, SeasonNumber, EpisodeNumber);
}
