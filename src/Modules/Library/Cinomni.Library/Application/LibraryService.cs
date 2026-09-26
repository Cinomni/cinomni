using Cinomni.Library.Contracts;
using Cinomni.Library.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Library.Application;

/// <summary>
/// Registers media assets from the import pipeline. The asset, its primary version, its relational
/// streams and its target links are written together with the <c>MediaAssetRegistered</c> event in
/// one unit of work. Idempotent by the asset id (minted by Import), so the at-least-once
/// delivery of <c>MediaAvailable</c> cannot duplicate an asset.
/// </summary>
public sealed class LibraryService(
    LibraryDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus) : ILibraryCommands
{
    public async Task RegisterMediaAssetAsync(RegisterMediaAssetRequest request, CancellationToken cancellationToken = default)
    {
        // Idempotent per asset — but NOT a bare early return. A redelivery may legitimately carry a
        // link the stored asset does not hold yet (the second episode of a multi-episode file), and
        // dropping it silently is unrecoverable without a manual correction.
        var existing = await dbContext.Assets
            .Include(a => a.TargetLinks)
            .Include(a => a.UnitLinks)
            .FirstOrDefaultAsync(a => a.Id == request.AssetId, cancellationToken);
        if (existing is not null)
        {
            await AddMissingLinksAsync(existing, request.TargetIds, request.UnitIds ?? [], cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var quality = QualityOf(request);
        var relativePath = Path.GetFileName(request.FullPath);
        var version = MediaVersion.Create(
            request.AssetId, relativePath, request.FullPath, request.Size, quality, releaseGroup: null, request.Streams,
            request.DurationSeconds, request.Bitrate);

        var asset = MediaAsset.Create(request.AssetId, request.WorkId, now);
        asset.AddVersion(version);
        foreach (var targetId in request.TargetIds)
        {
            asset.LinkTarget(targetId);
        }

        foreach (var unitId in request.UnitIds ?? [])
        {
            asset.LinkUnit(unitId);
        }

        var unitIds = asset.UnitLinks.Select(l => l.UnitId).ToList();
        var superseded = await SupersededAssetsAsync(request.WorkId, unitIds, request.FullPath, cancellationToken);

        await unitOfWork.ExecuteAsync(async token =>
        {
            // In the same transaction as the arrival: an episode served by two active assets is one the
            // library would offer twice, and the older file is already in the recycle folder by now, so
            // the second offer plays nothing. Saved first, on its own: the replacement usually takes the
            // very path the version it replaces names, and only a retired version stops holding that
            // path — an insert ordered ahead of the retirement would collide with it.
            foreach (var previous in superseded)
            {
                previous.MarkUpgraded(now);
            }

            await dbContext.SaveChangesAsync(token);

            dbContext.Assets.Add(asset); // new root → cascade inserts versions, streams and links
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(
                new MediaAssetRegistered(
                    asset.Id, asset.WorkId, version.Id, version.Streams.Count,
                    UnitIds: unitIds.Count == 0 ? null : unitIds),
                token);
        }, cancellationToken);
    }

    /// <summary>
    /// The active assets this arrival replaces: those serving any of the same catalog units, and the one
    /// of the same work whose live file sits at the very path the arrival does. Scoped to the work, since
    /// a unit id is a catalog id and means nothing outside it.
    /// <para>
    /// The path matters for assets registered before unit links existed: they serve no unit on record,
    /// and without it an upgrade landing on their path could never register. An arrival with neither
    /// replaces nothing — retiring by work id alone would take a whole series' episodes out with one file.
    /// </para>
    /// </summary>
    private async Task<List<MediaAsset>> SupersededAssetsAsync(
        Guid workId,
        IReadOnlyList<Guid> unitIds,
        string fullPath,
        CancellationToken cancellationToken)
    {
        return await dbContext.Assets
            .Include(a => a.Versions)
            .Where(a => a.WorkId == workId
                && a.State == MediaAssetState.Active
                && (a.UnitLinks.Any(l => unitIds.Contains(l.UnitId))
                    || a.Versions.Any(v => v.RetiredAt == null && v.FullPath == fullPath)))
            .ToListAsync(cancellationToken);
    }

    public async Task RelocateAssetFileAsync(
        Guid assetId,
        string fromPath,
        string toPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toPath))
        {
            return;
        }

        // Matched on the old path: the asset may carry more than one version, and only the one that
        // named the moved file is the one that moved. A redelivery finds nothing to match — the row
        // already says toPath — and writes nothing, which is what makes the command safe to repeat.
        var versions = await dbContext.Assets
            .Where(a => a.Id == assetId)
            .SelectMany(a => a.Versions)
            .Where(v => v.FullPath == fromPath)
            .ToListAsync(cancellationToken);
        if (versions.Count == 0)
        {
            return;
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            foreach (var version in versions)
            {
                version.Relocate(toPath);
            }

            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
    }

    public async Task LinkAssetUnitsAsync(
        Guid assetId,
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default)
    {
        if (unitIds.Count == 0)
        {
            return;
        }

        var asset = await dbContext.Assets
            .Include(a => a.UnitLinks)
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
        if (asset is null)
        {
            return; // unknown asset — nothing to link
        }

        await AddMissingLinksAsync(asset, [], unitIds, cancellationToken);
    }

    /// <summary>Adds only the links the asset does not already hold; writes nothing when there are none.</summary>
    private async Task AddMissingLinksAsync(
        MediaAsset asset,
        IReadOnlyList<Guid> targetIds,
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken)
    {
        var addedTargets = new List<AssetTargetLink>();
        foreach (var targetId in targetIds)
        {
            var before = asset.TargetLinks.Count;
            asset.LinkTarget(targetId);
            if (asset.TargetLinks.Count > before)
            {
                addedTargets.Add(asset.TargetLinks[^1]);
            }
        }

        var addedUnits = unitIds.Select(asset.LinkUnit).OfType<AssetUnitLink>().ToList();
        if (addedTargets.Count == 0 && addedUnits.Count == 0)
        {
            return;
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            // New children of a tracked aggregate (client-assigned keys) → add to their sets explicitly.
            dbContext.TargetLinks.AddRange(addedTargets);
            dbContext.UnitLinks.AddRange(addedUnits);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
    }

    public async Task AddExternalSubtitleAsync(Guid assetId, string language, bool forced, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.Assets
            .Include(a => a.Versions).ThenInclude(v => v.Streams)
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
        var version = asset?.Versions.FirstOrDefault(v => v.Id == asset.PrimaryVersionId) ?? asset?.Versions.FirstOrDefault();
        if (version is null)
        {
            return; // unknown asset or no version to attach to
        }

        // Idempotent: one external subtitle track per language.
        if (version.Streams.Any(s => s.Type == MediaStreamType.Subtitle && s.IsExternal
            && string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var nextIndex = version.Streams.Count == 0 ? 0 : version.Streams.Max(s => s.StreamIndex) + 1;
        var stream = MediaStream.ExternalSubtitle(version.Id, nextIndex, language, forced);
        version.Streams.Add(stream);

        await unitOfWork.ExecuteAsync(async token =>
        {
            // New child of a tracked aggregate (client-assigned key) → add to its set explicitly.
            dbContext.Streams.Add(stream);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// The version's quality, from both things that know part of it: the file's own geometry, which is
    /// the truthful resolution, and the release name, which is the only witness to the source.
    /// <para>
    /// Geometry wins on resolution — a release labelled 1080p that is really 720p is a lie the pixels
    /// settle. The source has no such arbiter, so it is taken as stated or left unknown.
    /// </para>
    /// </summary>
    private static MediaQuality QualityOf(RegisterMediaAssetRequest request)
    {
        var measured = MediaQuality.FromHeight(PrimaryVideoHeight(request.Streams));
        if (request.Quality is not { } release)
        {
            return measured;
        }

        return measured with
        {
            Source = release.Source,
            Modifier = release.Modifier,
            Revision = release.Revision,
            // Only when the file itself could not be measured: an unprobed file (an empty stream list
            // after a failed probe) still registers, and then the name is all there is.
            Resolution = measured.Resolution ?? release.Resolution,
        };
    }

    private static int? PrimaryVideoHeight(IReadOnlyList<MediaStreamInput> streams)
    {
        foreach (var stream in streams)
        {
            if (stream.Type == MediaStreamType.Video && stream.Height is > 0)
            {
                return stream.Height;
            }
        }

        return null;
    }
}
