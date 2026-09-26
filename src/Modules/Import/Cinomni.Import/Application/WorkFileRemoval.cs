using Cinomni.Import.Files;
using Cinomni.Library.Contracts;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Application;

/// <summary>
/// Deletes the library files of a work an administrator removed from the catalog together with its files.
/// Import does it because Import put them there and owns the library root; the paths come from Library's
/// record of what it holds for the work.
/// <para>
/// Every path is confined to the library root before anything is deleted — symbolic links resolved — so a
/// stored path that points anywhere else is refused and logged, never followed. An asset that was upgraded
/// away is skipped: the path it names is the one its replacement took, and the copy it described sits in
/// the recycle folder, which is the household's to empty.
/// </para>
/// <para>
/// Idempotent: a file already gone is not an error, so a redelivered command deletes what is left.
/// </para>
/// </summary>
public sealed class WorkFileRemoval(
    ILibraryQuery library,
    IImportFileSystem fileSystem,
    ImportOptions options,
    ILogger<WorkFileRemoval> logger)
{
    public async Task RemoveAsync(Guid workId, CancellationToken cancellationToken = default)
    {
        var assets = await library.GetByWorkAsync(workId, cancellationToken);
        var deleted = 0;
        foreach (var asset in assets.Where(a => a.State != MediaAssetState.Upgraded))
        {
            var detail = await library.GetAsync(asset.Id, cancellationToken);
            foreach (var version in detail?.Versions ?? [])
            {
                if (PathGuard.Confine(options.LibraryRoot, version.FullPath) is not { } confined)
                {
                    logger.LogWarning(
                        "Not deleting a file of removed work {WorkId}: its stored path is outside the library root.",
                        workId);
                    continue;
                }

                await fileSystem.DeleteAsync(confined, cancellationToken);
                deleted++;
            }
        }

        logger.LogInformation("Deleted {Count} library files of removed work {WorkId}.", deleted, workId);
    }
}
