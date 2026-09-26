namespace Cinomni.Library.Persistence;

/// <summary>
/// The N:M link between a media asset and a monitored target:
/// one file can satisfy several targets (a multi-episode file), and a season-pack replaces several
/// assets at once. The movie slice creates a single link; the many-to-many is exercised by the series
/// slice. <c>target_id</c> is an inter-schema reference to monitoring (no physical FK).
/// </summary>
public sealed class AssetTargetLink
{
    public Guid AssetId { get; init; }

    public Guid TargetId { get; init; }
}
