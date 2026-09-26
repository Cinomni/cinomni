using Cinomni.Downloads.Engine;
using Cinomni.Import.Application;
using Cinomni.Operations.Backup;
using Cinomni.Playback.Application;

namespace Cinomni.Host;

/// <summary>
/// Refuses a transcode root that overlaps the library or the download staging area, in either
/// direction. Playback deletes inside its root on its own initiative — every stream's output when it
/// ends, and every session-named directory no stream owns — so a root that shares a tree with media
/// puts somebody's files within reach of that sweep. Pure path arithmetic, so it runs in every
/// environment, beside the backup root's own check.
/// </summary>
internal static class TranscodeRootStartup
{
    /// <exception cref="InvalidOperationException">The roots overlap.</exception>
    public static void VerifyRootIsSeparate(IServiceProvider services)
    {
        var transcodeRoot = services.GetRequiredService<PlaybackOptions>().TranscodeRoot;

        List<(string Key, string? Path)> storageRoots =
        [
            ("Import:LibraryRoot", services.GetService<ImportOptions>()?.LibraryRoot),
            // Absent under the qBittorrent engine, which keeps its own storage.
            ("Downloads:Sidecar:StagingPath", services.GetService<SidecarOptions>()?.StagingPath),
        ];

        foreach (var (key, path) in storageRoots)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (BackupRoot.Contains(path, transcodeRoot) || BackupRoot.Contains(transcodeRoot, path))
            {
                throw new InvalidOperationException(
                    $"Playback:TranscodeRoot ('{transcodeRoot}') overlaps the storage root '{path}' (configuration "
                    + $"'{key}'). Playback deletes the output it no longer needs from its own root, so the two "
                    + "must not share a tree. Point Playback:TranscodeRoot at a directory of its own.");
            }
        }
    }
}
