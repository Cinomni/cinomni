using Cinomni.Kernel.Messaging;

namespace Cinomni.Library.Contracts;

/// <summary>Stable registered names of the Library integration events.</summary>
public static class LibraryEventNames
{
    public const string MediaAssetRegistered = "library.media-asset-registered";
}

/// <summary>
/// A media asset was registered in the library with its version and relational streams. Consumed by
/// Subtitles (start a subtitle search), Playback (cache), and Notifications. Carries a stream summary
/// count so a consumer can act without reloading. Ids travel as raw <see cref="Guid"/> for wire
/// stability. Keyed by the asset.
/// </summary>
/// <param name="UnitIds">
/// The catalog units the asset serves — the work id for a movie, one or more episode ids for a
/// series file. Without them a season pack reads as "Series X is available" N times with nothing
/// distinguishing the episodes, and every consumer would have to reach back into Library to find out
/// which episode it just got. Trailing optional: the payload is persisted as jsonb, so an in-flight
/// 1.x row deserializes with it null.
/// </param>
public sealed record MediaAssetRegistered(
    Guid AssetId,
    Guid WorkId,
    Guid VersionId,
    int StreamCount,
    IReadOnlyList<Guid>? UnitIds = null) : DomainEvent
{
    public override string IdempotencyKey => $"media-asset-registered:{AssetId}";
}
