using Cinomni.Library.Contracts;

namespace Cinomni.Library.Persistence;

/// <summary>
/// The Library aggregate root: the registered <b>media asset</b> — a file with its versions and
/// relational streams — tied to a catalog work and the monitored targets it serves. Its identity
/// is minted by Import (the asset id carried in <c>MediaAvailable</c>) but Library owns the rows. The
/// movie slice registers the asset in <see cref="MediaAssetState.Active"/> with a single primary
/// version; the scan/upgrade/missing transitions of the finite-state machine come with later slices.
/// </summary>
public sealed class MediaAsset
{
    public Guid Id { get; init; }

    /// <summary>The catalog work this asset belongs to (inter-schema reference, no physical FK).</summary>
    public Guid WorkId { get; init; }

    public MediaAssetState State { get; private set; }

    /// <summary>The version chosen for playback (set when the primary version is added).</summary>
    public Guid? PrimaryVersionId { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optimistic-concurrency counter (bumped on each state change).</summary>
    public int Version { get; private set; }

    public List<MediaVersion> Versions { get; } = [];

    public List<AssetTargetLink> TargetLinks { get; } = [];

    /// <summary>The catalog units this asset serves — one for a movie, N for a multi-episode file.</summary>
    public List<AssetUnitLink> UnitLinks { get; } = [];

    /// <summary>Creates an asset in <see cref="MediaAssetState.Active"/> from an import (⇠ MediaAvailable).</summary>
    public static MediaAsset Create(Guid assetId, Guid workId, DateTimeOffset now) => new()
    {
        Id = assetId,
        WorkId = workId,
        State = MediaAssetState.Active,
        CreatedAt = now,
        Version = 0,
    };

    /// <summary>Adds a version; the first one added becomes the primary (playable) version.</summary>
    public void AddVersion(MediaVersion version)
    {
        Versions.Add(version);
        PrimaryVersionId ??= version.Id;
    }

    /// <summary>
    /// Retires this asset because a better release replaced it. Only an active asset can be upgraded
    /// away, so a redelivered registration finds it already retired and changes nothing.
    /// <para>
    /// It stays in the library as a row: the file it points at is in the recycle folder rather than
    /// deleted, and a household that wants the old copy back needs the record of what it was. Its
    /// versions are retired with it (needs <see cref="Versions"/> loaded): the replacement lands on the
    /// path they name, and only live versions have to name a path no other live version does.
    /// </para>
    /// </summary>
    public bool MarkUpgraded(DateTimeOffset now)
    {
        if (State != MediaAssetState.Active)
        {
            return false;
        }

        State = MediaAssetState.Upgraded;
        foreach (var version in Versions)
        {
            version.Retire(now);
        }

        Version++;
        return true;
    }

    /// <summary>
    /// The work this asset served was removed from the catalog: a live asset (active, or missing its file)
    /// becomes <see cref="MediaAssetState.Removed"/> and its versions are retired, so nothing offers it for
    /// playback again. An asset already upgraded away, or already removed, is left as it is.
    /// </summary>
    /// <returns>True when the asset changed; false for a redelivery or an asset that was not live.</returns>
    public bool MarkRemoved(DateTimeOffset now)
    {
        if (State is not (MediaAssetState.Active or MediaAssetState.Missing))
        {
            return false;
        }

        State = MediaAssetState.Removed;
        foreach (var version in Versions)
        {
            version.Retire(now);
        }

        Version++;
        return true;
    }

    /// <summary>Links the asset to a monitored target it serves (idempotent per target).</summary>
    public void LinkTarget(Guid targetId)
    {
        if (targetId == Guid.Empty || TargetLinks.Any(l => l.TargetId == targetId))
        {
            return;
        }

        TargetLinks.Add(new AssetTargetLink { AssetId = Id, TargetId = targetId });
    }

    /// <summary>
    /// Links the asset to a catalog unit it serves (idempotent per unit, empty ids skipped). Returns
    /// the link when one was added, or null when it was already there — the caller needs to know,
    /// because only genuinely new links have to be inserted.
    /// </summary>
    public AssetUnitLink? LinkUnit(Guid unitId)
    {
        if (unitId == Guid.Empty || UnitLinks.Any(l => l.UnitId == unitId))
        {
            return null;
        }

        var link = new AssetUnitLink { AssetId = Id, UnitId = unitId };
        UnitLinks.Add(link);
        return link;
    }
}
