using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;

namespace Cinomni.Library.Messaging;

/// <summary>Stable registered names of the Library commands.</summary>
public static class LibraryCommandNames
{
    public const string RegisterMediaAsset = "library.register-media-asset";
    public const string AddExternalSubtitle = "library.add-external-subtitle";
    public const string LinkAssetUnits = "library.link-asset-units";
    public const string RelocateAssetFile = "library.relocate-asset-file";
    public const string RemoveWorkAssets = "library.remove-work-assets";
}

/// <summary>
/// Points a version at the path its file was moved to — enqueued by Import's
/// <c>MediaFileRelocated</c> reaction. Idempotent by <c>relocate-asset-file:{assetId}</c>, and
/// idempotent again in the handler: the update matches on the old path, so a second delivery finds
/// the row already correct and writes nothing.
/// </summary>
public sealed record RelocateAssetFileCommand(Guid AssetId, string FromPath, string ToPath) : ICommand;

/// <summary>
/// Adds an external subtitle track to an asset — enqueued by Subtitles' <c>SubtitleAvailable</c>
/// reaction. Idempotent by <c>add-external-subtitle:{subtitleId}</c>.
/// </summary>
public sealed record AddExternalSubtitleCommand(Guid AssetId, string Language, bool Forced) : ICommand;

/// <summary>
/// Registers a media asset with its version and relational streams — enqueued by Import's
/// <c>MediaAvailable</c> reaction. Carries everything needed to persist the rows (the event handler
/// runs in the relay's transaction and cannot write the library schema itself). Idempotent by
/// <c>register-media-asset:{assetId}</c>.
/// </summary>
/// <param name="UnitIds">
/// The catalog units the asset serves. Trailing optional: the command queue persists this payload as
/// jsonb, so an in-flight 1.x row deserializes with it null and the asset simply carries no unit link.
/// </param>
/// <param name="Quality">
/// The release's structural quality, when Import could read it. Trailing optional for the same reason:
/// a queued row written before this field existed deserializes with it null, and the version it
/// registers keeps an unknown source rather than a wrong one.
/// </param>
public sealed record RegisterMediaAssetCommand(
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
    long? Bitrate = null) : ICommand;

/// <summary>
/// Links an existing asset to catalog units discovered after registration. Idempotent per
/// (asset, unit); the caller supplies an idempotency key that changes with the unit set.
/// </summary>
public sealed record LinkAssetUnitsCommand(Guid AssetId, IReadOnlyList<Guid> UnitIds) : ICommand;

/// <summary>Retires the assets of a work removed from the catalog (⇠ Catalog's <c>WorkRemoved</c>).</summary>
public sealed record RemoveWorkAssetsCommand(Guid WorkId) : ICommand;
