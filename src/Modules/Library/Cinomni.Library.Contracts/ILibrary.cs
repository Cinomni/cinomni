namespace Cinomni.Library.Contracts;

/// <summary>One elementary stream to register, in Library's own vocabulary (mapped from the probe result).</summary>
public sealed record MediaStreamInput(
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
    bool IsForced);

/// <summary>
/// The structural quality of the release a file came from, as plain wire values (the enum names
/// ReleaseParsing uses). Library records it but does not interpret it: what a given source is worth is
/// the acquisition profile's opinion, and that belongs to Decision.
/// <para>
/// It cannot be derived from the file. A video stream's geometry gives the resolution and nothing else,
/// so a WEB-DL and a Bluray of the same title are indistinguishable once imported — which is precisely
/// the distinction an upgrade has to make. Only the release name carries it, and only Import still sees
/// that name: a series file is renamed to <c>Series - S01E01 - Title</c> on the way in.
/// </para>
/// </summary>
public sealed record ReleaseQuality(string? Source, string? Resolution, string? Modifier, int Revision = 1);

/// <summary>Everything Library needs to register an asset, carried from Import's MediaAvailable.</summary>
/// <param name="TargetIds">The monitored targets that acquired the asset (the acquisition side).</param>
/// <param name="UnitIds">
/// The catalog units the asset serves — the work id for a movie, one or more episode ids for a
/// series file. Trailing optional so existing construction sites are unaffected; the relational
/// unit links that consume it land with the series slice.
/// </param>
/// <param name="Quality">
/// The release's structural quality, when Import could parse it. Null leaves the version's source
/// unknown, which reads as "not judgeable" rather than "poor" — an unknown version is never replaced.
/// </param>
/// <param name="DurationSeconds">The probed runtime; null when unknown. Trailing optional.</param>
/// <param name="Bitrate">The probed overall bitrate in bits per second; null when unknown. Trailing optional.</param>
public sealed record RegisterMediaAssetRequest(
    Guid AssetId,
    Guid WorkId,
    IReadOnlyList<Guid> TargetIds,
    string FullPath,
    long Size,
    string Container,
    IReadOnlyList<MediaStreamInput> Streams,
    IReadOnlyList<Guid>? UnitIds = null,
    ReleaseQuality? Quality = null,
    double? DurationSeconds = null,
    long? Bitrate = null);

/// <summary>
/// Write surface of the Library module. Registration is idempotent: keyed by the asset id (minted by
/// Import), a redelivered <c>MediaAvailable</c> reuses the existing asset rather than duplicating it.
/// </summary>
public interface ILibraryCommands
{
    /// <summary>
    /// Registers a media asset with its primary version and relational streams (⇠ MediaAvailable),
    /// linking it to the monitored targets <em>and</em> the catalog units it serves, and emitting
    /// <c>MediaAssetRegistered</c>.
    /// <para>
    /// Idempotent but <b>not</b> a bare no-op when the asset already exists: any link the request
    /// carries and the asset does not yet hold is added. Returning early instead would silently drop
    /// the second episode of a multi-episode file whose links arrived in two deliveries.
    /// </para>
    /// </summary>
    Task RegisterMediaAssetAsync(RegisterMediaAssetRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links an existing asset to catalog units discovered after it was registered (a later structure
    /// sync, or a manual correction). Idempotent per (asset, unit); no-op if the asset is unknown.
    /// </summary>
    Task LinkAssetUnitsAsync(Guid assetId, IReadOnlyList<Guid> unitIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enriches an asset with an external (sidecar) subtitle stream (⇠ Subtitles' <c>SubtitleAvailable</c>).
    /// Idempotent per (asset, language): a redelivery does not add a duplicate track. No-op if the asset
    /// is unknown.
    /// </summary>
    Task AddExternalSubtitleAsync(Guid assetId, string language, bool forced, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that an asset's file has been moved on disk (⇠ Import's <c>MediaFileRelocated</c>),
    /// which is the only way a stored path legitimately changes. Matched on the old path rather than
    /// applied blindly, so a redelivery after the row is already correct changes nothing, and a
    /// version pointing somewhere else entirely is left alone.
    /// </summary>
    Task RelocateAssetFileAsync(
        Guid assetId,
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default);
}

/// <summary>Read model of media assets, their versions and streams.</summary>
public interface ILibraryQuery
{
    Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<MediaAssetDetail?> GetAsync(MediaAssetId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every asset of a work. For a series this returns every episode asset with nothing to tell them
    /// apart — use <see cref="GetByUnitAsync"/> when the caller means a specific episode.
    /// </summary>
    Task<IReadOnlyList<MediaAssetSummary>> GetByWorkAsync(Guid workId, CancellationToken cancellationToken = default);

    /// <summary>The assets that play one catalog unit (an episode, or a movie's work).</summary>
    Task<IReadOnlyList<MediaAssetSummary>> GetByUnitAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The assets of several units in one round trip — a season view needs the playable file of every
    /// episode at once, and one query per episode is an N+1 on the page that renders most often.
    /// </summary>
    Task<IReadOnlyList<MediaAssetSummary>> GetByUnitsAsync(
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The quality of what is currently playable for a unit (an episode, or a movie's work), so a
    /// decision about acquiring a better release can be made against it. Null when nothing is on disk
    /// for it, which is the ordinary missing case and not an error.
    /// <para>
    /// Only <see cref="MediaAssetState.Active"/> assets answer: a version that has already been upgraded
    /// away is history, and letting it speak would compare a candidate against a file nobody can play.
    /// </para>
    /// </summary>
    Task<ReleaseQuality?> GetCurrentQualityAsync(Guid unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every path the library currently believes a playable file sits at. Active assets only: an
    /// upgraded-away version names a file that was moved to the recycle folder when its replacement
    /// landed, so repairing that path would chase a file nobody can play.
    /// </summary>
    Task<IReadOnlyList<MediaVersionPath>> ListActiveVersionPathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The active asset whose live version sits at exactly <paramref name="fullPath"/>, or null when no
    /// playable file of the library is there. Import asks before it lands a file: a path held by another
    /// title is somebody else's film, not a copy to replace.
    /// </summary>
    Task<MediaAssetSummary?> FindActiveByPathAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The works that have a playable file anywhere under <paramref name="directory"/>. Import asks
    /// before it lands in a title's folder: a folder another title already has files in is not shared.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindActiveWorksUnderAsync(string directory, CancellationToken cancellationToken = default);
}
