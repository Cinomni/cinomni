using Cinomni.Kernel.Identifiers;

namespace Cinomni.Library.Contracts;

/// <summary>Stable internal identity of a media asset (UUIDv7). Minted by Import, owned by Library.</summary>
public readonly record struct MediaAssetId(Guid Value)
{
    public static MediaAssetId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable internal identity of a media version (UUIDv7).</summary>
public readonly record struct MediaVersionId(Guid Value)
{
    public static MediaVersionId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The lifecycle of a media asset. It becomes <see cref="Active"/> when a download is
/// imported (⇠ MediaAvailable), goes <see cref="Missing"/> if a scan finds its file gone (without
/// dropping the record), <see cref="Upgraded"/> when a better version replaces it, and
/// <see cref="Removed"/> once confirmed gone. Only the entry to Active is exercised in the movie slice;
/// scans and upgrades are later.
/// </summary>
public enum MediaAssetState
{
    Active = 1,
    Upgraded = 2,
    Missing = 3,
    Removed = 4,
}

/// <summary>The kind of an elementary media stream (stored relationally, not as JSON).</summary>
public enum MediaStreamType
{
    Video = 1,
    Audio = 2,
    Subtitle = 3,
}

/// <summary>The dynamic-range classification of a video stream (nullable for non-video / unknown).</summary>
public enum VideoRangeType
{
    Sdr = 1,
    Hdr10 = 2,
    Hdr10Plus = 3,
    DoVi = 4,
    Hlg = 5,
}

/// <summary>Projection of one relational media stream.</summary>
public sealed record MediaStreamSummary(
    int StreamIndex,
    MediaStreamType Type,
    string? Codec,
    string? Language,
    int? Channels,
    int? Width,
    int? Height,
    int? BitDepth,
    VideoRangeType? VideoRangeType,
    bool IsDefault,
    bool IsForced,
    bool IsExternal);

/// <summary>
/// One stored file path with the asset and version carrying it. Deliberately the whole of what a
/// path-repair pass needs and nothing else: it enumerates the library, so anything richer would
/// materialise every asset's streams to move a file.
/// </summary>
public sealed record MediaVersionPath(Guid AssetId, Guid VersionId, string FullPath);

/// <summary>
/// Projection of one media version (an edition/stack of a file) and its streams.
/// <see cref="DurationSeconds"/> and <see cref="Bitrate"/> (bits per second) are what the probe
/// measured, null when it could not say; trailing optional so existing construction sites are unaffected.
/// </summary>
public sealed record MediaVersionSummary(
    MediaVersionId Id,
    string RelativePath,
    string FullPath,
    long Size,
    string? ReleaseGroup,
    IReadOnlyList<MediaStreamSummary> Streams,
    double? DurationSeconds = null,
    long? Bitrate = null);

/// <summary>
/// Projection of a media asset's current state. <see cref="UnitIds"/> is trailing optional so
/// existing construction sites are unaffected; it is what tells two episode assets of one series
/// apart, which <see cref="WorkId"/> alone cannot.
/// </summary>
public sealed record MediaAssetSummary(
    MediaAssetId Id,
    Guid WorkId,
    MediaAssetState State,
    MediaVersionId? PrimaryVersionId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<Guid>? UnitIds = null);

/// <summary>
/// The full view of an asset: its state, its versions (with streams), the monitored targets that
/// acquired it and the catalog units it serves. The two id lists are distinct id spaces that happen
/// to coincide for a movie; <see cref="UnitIds"/> is trailing optional.
/// </summary>
public sealed record MediaAssetDetail(
    MediaAssetSummary Asset,
    IReadOnlyList<MediaVersionSummary> Versions,
    IReadOnlyList<Guid> TargetIds,
    IReadOnlyList<Guid>? UnitIds = null);
