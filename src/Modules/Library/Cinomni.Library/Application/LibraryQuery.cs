using Cinomni.Library.Contracts;
using Cinomni.Library.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Library.Application;

/// <summary>
/// Read model over media assets, their versions and relational streams, with no viewer: it answers for
/// the system, so it applies no access filter. Event and command handlers bind this one; endpoints bind
/// <see cref="LibraryBrowse"/>, which scopes to what the caller may see.
/// </summary>
public sealed class LibraryQuery(LibraryDbContext dbContext) : ILibraryQuery
{
    public async Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var assets = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.UnitLinks)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
        return assets.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<MediaAssetSummary>> GetByWorkAsync(Guid workId, CancellationToken cancellationToken = default)
    {
        var assets = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.UnitLinks)
            .Where(a => a.WorkId == workId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
        return assets.Select(ToSummary).ToList();
    }

    public Task<IReadOnlyList<MediaAssetSummary>> GetByUnitAsync(Guid unitId, CancellationToken cancellationToken = default) =>
        GetByUnitsAsync([unitId], cancellationToken);

    public async Task<IReadOnlyList<MediaAssetSummary>> GetByUnitsAsync(
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default)
    {
        if (unitIds.Count == 0)
        {
            return [];
        }

        // One round trip for a whole season: the per-episode UI would otherwise be an N+1 on the
        // page that renders most often.
        var assets = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.UnitLinks)
            .Where(a => a.UnitLinks.Any(l => unitIds.Contains(l.UnitId)))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
        return assets.Select(ToSummary).ToList();
    }

    public async Task<ReleaseQuality?> GetCurrentQualityAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        // Active only, and the primary version only: an upgraded-away version is history, and a
        // secondary one is not what plays. Newest first, because a unit served by more than one active
        // asset (a re-import that has not been reconciled) should answer with what arrived last.
        var quality = await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.State == MediaAssetState.Active && a.UnitLinks.Any(l => l.UnitId == unitId))
            .OrderByDescending(a => a.CreatedAt)
            .SelectMany(a => a.Versions.Where(v => v.Id == a.PrimaryVersionId))
            .Select(v => v.QualityJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (quality is null)
        {
            return null;
        }

        var stored = MediaQuality.FromJson(quality);

        // A version with no source was imported before the release name was carried through, or from a
        // name that never said. Answering with a half-known quality would let a caller read "unknown"
        // as "poor" and replace a perfectly good file; answering null says plainly that we do not know.
        return stored.Source is null
            ? null
            : new ReleaseQuality(stored.Source, stored.Resolution, stored.Modifier, stored.Revision);
    }

    public async Task<IReadOnlyList<MediaVersionPath>> ListActiveVersionPathsAsync(
        CancellationToken cancellationToken = default)
    {
        // Projected, not materialised: this enumerates the whole library, and the caller only moves
        // files. Ordered so a repair pass interrupted half-way resumes over the same sequence — and
        // ordered on the column, before the projection, because a sort key read off an object the
        // projection constructs is not something the provider can turn into SQL. The order is total:
        // a version's full path is unique (ux_media_versions_full_path).
        return await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.State == MediaAssetState.Active)
            .SelectMany(a => a.Versions)
            .OrderBy(v => v.FullPath)
            .Select(v => new MediaVersionPath(v.AssetId, v.Id, v.FullPath))
            .ToListAsync(cancellationToken);
    }

    public async Task<MediaAssetSummary?> FindActiveByPathAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.UnitLinks)
            .Where(a => a.State == MediaAssetState.Active
                && a.Versions.Any(v => v.FullPath == fullPath && v.RetiredAt == null))
            .FirstOrDefaultAsync(cancellationToken);
        return asset is null
            ? null
            : new MediaAssetSummary(
                new MediaAssetId(asset.Id), asset.WorkId, asset.State,
                asset.PrimaryVersionId is { } primary ? new MediaVersionId(primary) : null,
                asset.CreatedAt, [.. asset.UnitLinks.Select(l => l.UnitId)]);
    }

    public async Task<IReadOnlyList<Guid>> FindActiveWorksUnderAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.State == MediaAssetState.Active
                && a.Versions.Any(v => v.RetiredAt == null && v.FullPath.StartsWith(prefix)))
            .Select(a => a.WorkId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<MediaAssetDetail?> GetAsync(MediaAssetId id, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.Versions).ThenInclude(v => v.Streams)
            .Include(a => a.TargetLinks)
            .Include(a => a.UnitLinks)
            .FirstOrDefaultAsync(a => a.Id == id.Value, cancellationToken);

        return asset is null ? null : BuildDetail(asset);
    }

    private static MediaAssetDetail BuildDetail(MediaAsset asset)
    {
        var versions = asset.Versions
            .Select(v => new MediaVersionSummary(
                new MediaVersionId(v.Id),
                v.RelativePath,
                v.FullPath,
                v.Size,
                v.ReleaseGroup,
                v.Streams
                    .OrderBy(s => s.StreamIndex)
                    .Select(ToStreamSummary)
                    .ToList(),
                v.DurationSeconds,
                v.Bitrate))
            .ToList();

        var targetIds = asset.TargetLinks.Select(l => l.TargetId).ToList();
        var unitIds = asset.UnitLinks.Select(l => l.UnitId).ToList();
        return new MediaAssetDetail(ToSummary(asset), versions, targetIds, unitIds);
    }

    /// <summary>Shared with the viewer-scoped twin, which decides visibility and then maps the same way.</summary>
    internal static MediaAssetDetail ToDetail(MediaAsset asset) => BuildDetail(asset);

    internal static MediaAssetSummary ToSummary(MediaAsset asset) => new(
        new MediaAssetId(asset.Id),
        asset.WorkId,
        asset.State,
        asset.PrimaryVersionId is { } versionId ? new MediaVersionId(versionId) : null,
        asset.CreatedAt,
        [.. asset.UnitLinks.Select(l => l.UnitId)]);

    private static MediaStreamSummary ToStreamSummary(MediaStream stream) => new(
        stream.StreamIndex,
        stream.Type,
        stream.Codec,
        stream.Language,
        stream.Channels,
        stream.Width,
        stream.Height,
        stream.BitDepth,
        stream.VideoRangeType,
        stream.IsDefault,
        stream.IsForced,
        stream.IsExternal);
}
