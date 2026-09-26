using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.Extensions.Logging;

namespace Cinomni.Import.Application;

/// <summary>What the repair pass found, or did, for one stored path.</summary>
public enum PathRepairOutcome
{
    /// <summary>The name needs repairing and the file is where the library says it is (preview only).</summary>
    Repairable = 1,

    /// <summary>The file was moved to the sanitised path by this pass.</summary>
    Repaired = 2,

    /// <summary>
    /// The file was already at the sanitised path with the rows still naming the old one — an earlier
    /// pass moved it and did not get to announce it. The announcement was made now.
    /// </summary>
    Announced = 3,

    /// <summary>
    /// Something is already at the sanitised path while the original is also still there. Two distinct
    /// files claim one name, so nothing was moved.
    /// </summary>
    Blocked = 4,

    /// <summary>Neither path exists. The row names a file that is not on disk; not this pass's problem.</summary>
    Absent = 5,
}

/// <summary>One stored path the pass considered, and what became of it.</summary>
/// <param name="Sidecars">Files moved alongside the video (subtitles and anything else named from its stem).</param>
public sealed record PathRepairEntry(
    Guid AssetId,
    string FromPath,
    string ToPath,
    PathRepairOutcome Outcome,
    int Sidecars = 0);

/// <summary>Everything one pass considered. An empty list means the library needs no repair.</summary>
public sealed record PathRepairReport(IReadOnlyList<PathRepairEntry> Entries);

/// <summary>
/// Repairs library names an earlier build wrote when <see cref="PathGuard.SanitizeName"/> was built on
/// <c>Path.GetInvalidFileNameChars()</c> and so was very nearly a no-op on Linux. It is a data
/// migration, not part of the import spine: nothing calls it automatically, because renaming files in
/// a library that works is its owner's decision — the same reasoning that ships upgrades switched off.
/// <para>
/// <b>Order matters and is deliberate.</b> The file moves first, the rows follow by event. A crash
/// between the two leaves the file at the sanitised path with the library still naming the old one,
/// which the next pass recognises (<see cref="PathRepairOutcome.Announced"/>) and announces after the
/// fact. Doing it the other way round would leave rows naming a path that does not exist yet, with
/// nothing able to tell that from a file someone deleted.
/// </para>
/// <para>
/// <b>It never overwrites.</b> A sanitised path already occupied by a different file is reported
/// blocked and left alone: two names that differ only by a forbidden character legitimately collapse
/// onto one, and picking a winner would destroy a household's file to tidy a name.
/// </para>
/// </summary>
public sealed class LibraryPathRepair(
    ILibraryQuery library,
    IImportFileSystem fileSystem,
    LibraryPathSanitizer sanitizer,
    IEventBus eventBus,
    IUnitOfWork unitOfWork,
    ImportOptions options,
    ILogger<LibraryPathRepair> logger)
{
    /// <summary>
    /// What a repair would do, touching nothing. The operator sees the whole list — including what is
    /// blocked and what is absent — before deciding, which is the point of running it first.
    /// </summary>
    public async Task<PathRepairReport> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<PathRepairEntry>();
        foreach (var stored in await CandidatesAsync(cancellationToken))
        {
            entries.Add(new PathRepairEntry(
                stored.AssetId,
                stored.FromPath,
                stored.ToPath,
                Classify(stored),
                SiblingsOf(stored.FromPath).Count));
        }

        return new PathRepairReport(entries);
    }

    /// <summary>
    /// Performs the repair. Idempotent and resumable: each file is considered on its own, a pass that
    /// is interrupted leaves every file it had not reached exactly as it was, and running it again
    /// finishes the job rather than repeating it.
    /// </summary>
    public async Task<PathRepairReport> RepairAsync(CancellationToken cancellationToken = default)
    {
        // A repair may not write into a root that is not mounted: every path would look absent and the
        // pass would report a clean library while the files sat on an unmounted volume.
        if (!fileSystem.RootAccessible(options.LibraryRoot))
        {
            logger.LogWarning("The library root is not accessible; no path was repaired.");
            return new PathRepairReport([]);
        }

        var entries = new List<PathRepairEntry>();
        foreach (var stored in await CandidatesAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await RepairOneAsync(stored, cancellationToken));
        }

        return new PathRepairReport(entries);
    }

    /// <summary>The stored paths whose names the current rules would write differently.</summary>
    private async Task<List<StoredPath>> CandidatesAsync(CancellationToken cancellationToken)
    {
        var paths = await library.ListActiveVersionPathsAsync(cancellationToken);
        return paths
            .Select(p => (Path: p, Sanitised: sanitizer.SanitisedPathFor(p.FullPath)))
            .Where(pair => pair.Sanitised is not null)
            .Select(pair => new StoredPath(pair.Path.AssetId, pair.Path.FullPath, pair.Sanitised!))
            .ToList();
    }

    private PathRepairOutcome Classify(StoredPath stored)
    {
        var atOld = fileSystem.FileExists(stored.FromPath);
        var atNew = fileSystem.FileExists(stored.ToPath);

        return (atOld, atNew) switch
        {
            (true, false) => PathRepairOutcome.Repairable,
            (false, true) => PathRepairOutcome.Announced,
            (true, true) => PathRepairOutcome.Blocked,
            _ => PathRepairOutcome.Absent,
        };
    }

    private async Task<PathRepairEntry> RepairOneAsync(StoredPath stored, CancellationToken cancellationToken)
    {
        var outcome = Classify(stored);
        var sidecars = SiblingsOf(stored.FromPath);

        switch (outcome)
        {
            case PathRepairOutcome.Blocked:
                logger.LogWarning(
                    "A library path could not be repaired: the sanitised name is already taken by another file. "
                    + "Asset {AssetId}.",
                    stored.AssetId);
                return new PathRepairEntry(stored.AssetId, stored.FromPath, stored.ToPath, outcome, sidecars.Count);

            case PathRepairOutcome.Absent:
                return new PathRepairEntry(stored.AssetId, stored.FromPath, stored.ToPath, outcome, 0);

            case PathRepairOutcome.Announced:
                // The move already happened; only the announcement is missing. Nothing touches disk.
                await AnnounceAsync(stored, cancellationToken);
                return new PathRepairEntry(stored.AssetId, stored.FromPath, stored.ToPath, outcome, 0);

            default:
                // Every sidecar has to be able to follow before anything moves. Subtitles renames its rows
                // by the video's new name when it hears of the move, so a sidecar left behind would have
                // its row pointed at whatever file took its new name — and nothing would ever correct it.
                if (sidecars.Any(sidecar => fileSystem.FileExists(sidecar.To)))
                {
                    logger.LogWarning(
                        "A library path could not be repaired: a sidecar's sanitised name is already taken by another file. "
                        + "Asset {AssetId}.",
                        stored.AssetId);
                    return new PathRepairEntry(stored.AssetId, stored.FromPath, stored.ToPath, PathRepairOutcome.Blocked, sidecars.Count);
                }

                // Filesystem first and outside any transaction, as every external effect is. The sidecars
                // go with the video: they are named from its stem, so leaving them would strand a
                // subtitle under a name nothing looks up again.
                await fileSystem.EnsureDirectoryAsync(
                    Path.GetDirectoryName(stored.ToPath)!, cancellationToken);
                try
                {
                    await fileSystem.MoveAsync(stored.FromPath, stored.ToPath, cancellationToken);
                }
                catch (IOException)
                {
                    // Taken between the look and the move: the same answer as a path taken before it.
                    logger.LogWarning(
                        "A library path could not be repaired: the sanitised name was taken while moving. Asset {AssetId}.",
                        stored.AssetId);
                    return new PathRepairEntry(
                        stored.AssetId, stored.FromPath, stored.ToPath, PathRepairOutcome.Blocked, sidecars.Count);
                }

                var moved = 0;
                foreach (var (from, to) in sidecars)
                {
                    moved += await TryMoveSidecarAsync(stored, from, to, cancellationToken) ? 1 : 0;
                }

                await AnnounceAsync(stored, cancellationToken);
                logger.LogInformation(
                    "Repaired a library path for asset {AssetId}, with {Sidecars} sidecar file(s).",
                    stored.AssetId,
                    moved);
                return new PathRepairEntry(
                    stored.AssetId, stored.FromPath, stored.ToPath, PathRepairOutcome.Repaired, moved);
        }
    }

    /// <summary>
    /// Moves one sidecar with its video, unless its new name is already taken: the file there is
    /// somebody's — another video's subtitle, most likely — and never overwritten. The sidecar is left
    /// where it was instead, and that is logged, because it no longer follows its video's name.
    /// </summary>
    private async Task<bool> TryMoveSidecarAsync(StoredPath stored, string from, string to, CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(to))
        {
            try
            {
                await fileSystem.MoveAsync(from, to, cancellationToken);
                return true;
            }
            catch (IOException)
            {
                // Taken between the look and the move: left where it is, like one taken before.
            }
        }

        logger.LogWarning(
            "A sidecar of asset {AssetId} was left under its old name: its new one is already taken by another file.",
            stored.AssetId);
        return false;
    }

    /// <summary>
    /// Publishes the fact in its own unit of work, so the outbox row commits and the consumers correct
    /// their rows exactly once. No module state of Import's own changes — the library tree is the state,
    /// and it changed on disk a moment ago.
    /// </summary>
    private Task AnnounceAsync(StoredPath stored, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            token => eventBus.PublishAsync(
                new MediaFileRelocated(stored.AssetId, stored.FromPath, stored.ToPath), token),
            cancellationToken);

    /// <summary>
    /// The files that travel with the video: everything directly beside it named <c>&lt;stem&gt;.…</c> —
    /// the subtitle sidecars, and anything else an installation writes the same way. Each keeps whatever
    /// follows the stem, so <c>film.es.forced.srt</c> stays the Spanish forced track. The dot matters:
    /// <c>film 2.mkv</c> or <c>film-extras.nfo</c> merely start with the same letters. And another video is
    /// never a sidecar, however it is named — <c>film.extended.mkv</c> is a film of its own.
    /// <para>
    /// Deliberately derived from the name rather than asked of Subtitles: Import owns the library tree
    /// and moves what is in it. Subtitles owns the rows that point at those files and recomposes its
    /// own paths the same way when it hears the move.
    /// </para>
    /// </summary>
    private List<(string From, string To)> SiblingsOf(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (directory is null || stem.Length == 0 || !fileSystem.DirectoryExists(directory))
        {
            return [];
        }

        var sanitisedVideo = sanitizer.SanitisedPathFor(videoPath);
        if (sanitisedVideo is null)
        {
            return [];
        }

        var targetDirectory = Path.GetDirectoryName(sanitisedVideo)!;
        var targetStem = Path.GetFileNameWithoutExtension(sanitisedVideo);

        return fileSystem.EnumerateFiles(directory)
            .Select(entry => entry.Path)
            // Directly beside the video, not somewhere below it: EnumerateFiles walks the whole subtree,
            // and a season folder's files are not this video's sidecars.
            .Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.Ordinal))
            .Where(path => !string.Equals(path, videoPath, StringComparison.Ordinal))
            .Where(path => Path.GetFileName(path).StartsWith(stem + ".", StringComparison.Ordinal))
            .Where(path => !options.VideoExtensions.Contains(Path.GetExtension(path)))
            .Select(path => (
                From: PathGuard.Confine(options.LibraryRoot, path),
                To: PathGuard.Confine(
                    options.LibraryRoot,
                    Path.Combine(targetDirectory, targetStem + Path.GetFileName(path)[stem.Length..]))))
            // Both ends confined, and a pair that will not confine is dropped rather than moved. The
            // tail comes off a name that arrived in a torrent, and this pass is the one place that
            // composes a path from a string already on disk rather than from the catalogue.
            .Where(pair => pair.From is not null && pair.To is not null)
            .Select(pair => (From: pair.From!, To: pair.To!))
            .ToList();
    }

    /// <summary>One stored path and where it should be, once the sanitiser has had its say.</summary>
    private sealed record StoredPath(Guid AssetId, string FromPath, string ToPath);
}
