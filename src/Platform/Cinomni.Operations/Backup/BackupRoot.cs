namespace Cinomni.Operations.Backup;

/// <summary>
/// Validates the configured backup directory and answers whether one directory sits inside another.
/// <para>
/// This is <b>configuration validation</b>, not untrusted-path confinement. Nothing here ever sees a
/// value from a request, a provider or a torrent: the only input is a path an operator wrote in their
/// own settings file, and the question being answered is "did they put the dumps somewhere that will
/// hurt them later". Import's <c>PathGuard</c> answers the different, hostile-input question, and it
/// stays where <c>SECURITY.md</c> puts it — the platform kernel may not reference a module, so reaching
/// for it here is not available in any case. The handoff records folding the two together in the kernel
/// as follow-up work.
/// </para>
/// <para>
/// The store itself never accepts a caller-supplied path at all: it enumerates its own root and matches
/// a closed file-name pattern, which is what keeps the only deleting code in the product bounded.
/// </para>
/// </summary>
public static class BackupRoot
{
    /// <summary>
    /// Resolves <paramref name="path"/> to a canonical absolute directory, refusing the spellings that
    /// cannot mean a local directory on this node: a blank value, a UNC share or a device path.
    /// A relative path is accepted and resolved against the process' working directory — a development
    /// convenience — which is why the resolved value is what gets logged at startup.
    /// </summary>
    /// <param name="configurationKey">Named in the failure so the operator knows which key to fix.</param>
    /// <exception cref="InvalidOperationException">The value cannot name a local directory.</exception>
    public static string Canonicalize(string configurationKey, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Configuration '{configurationKey}' must name a directory; it is not set.");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration '{configurationKey}' must name a local directory. UNC shares and device "
                + $"paths are refused (configured: '{path}').");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Configuration '{configurationKey}' is not a usable directory path (configured: '{path}').",
                exception);
        }
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> itself or a directory beneath it.
    /// Both sides are canonicalised and, where the directory exists, resolved through symbolic links
    /// first, so mixed separators, <c>..</c> segments and a link into a media tree cannot hide a
    /// containment that really exists.
    /// <para>
    /// What it still cannot see is bind-mount aliasing: two container paths backed by the same host
    /// directory are different paths and different inodes to everything reachable from here. In the
    /// packaged deployment the container's paths are fixed, so keeping the <em>host</em> path out of the
    /// media tree is the operator's own decision — <c>DEPLOYMENT.md</c> says so plainly rather than
    /// implying a guarantee this cannot make.
    /// </para>
    /// </summary>
    public static bool Contains(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string canonicalRoot;
        string canonicalCandidate;
        try
        {
            canonicalRoot = Resolve(root);
            canonicalCandidate = Resolve(candidate);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return canonicalCandidate.Equals(canonicalRoot, comparison)
            || canonicalCandidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// The absolute path with <c>..</c> and mixed separators removed, followed through symbolic links
    /// when the directory exists. A path that is not there yet — the ordinary case at first startup —
    /// resolves lexically, which is the most this can honestly say about a directory that has no target.
    /// </summary>
    private static string Resolve(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        try
        {
            var target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
            return target is null ? full : Path.TrimEndingDirectorySeparator(target.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A link that cannot be followed — a broken target, or a directory this account may not
            // traverse — leaves the lexical answer, which is what this check gave before.
            return full;
        }
    }

    /// <summary>
    /// Refuses a backup root that overlaps a storage root in either direction.
    /// <para>
    /// Downwards matters because a dump is a credential store and every media root is read by a surface
    /// that serves files. Upwards matters because retention deletes inside the backup root, and an
    /// operator who nests their library under it has put media within reach of the only code in the
    /// product that removes data nobody asked to remove.
    /// </para>
    /// </summary>
    /// <param name="backupRoot">The canonical backup root.</param>
    /// <param name="storageRoots">Configuration key and path of each storage root, for the message.</param>
    /// <exception cref="InvalidOperationException">The backup root overlaps a storage root.</exception>
    public static void VerifySeparateFrom(
        string backupRoot,
        IEnumerable<(string ConfigurationKey, string Path)> storageRoots)
    {
        ArgumentNullException.ThrowIfNull(storageRoots);

        foreach (var (key, path) in storageRoots)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Contains(path, backupRoot))
            {
                throw new InvalidOperationException(
                    $"Backup:Root ('{backupRoot}') is inside the storage root '{path}' (configuration "
                    + $"'{key}'). A database dump contains delivery-channel URLs, indexer addresses and "
                    + "password verifiers, so it must never sit in a directory the media surfaces serve "
                    + "from. Point Backup:Root at a directory outside every storage root.");
            }

            if (Contains(backupRoot, path))
            {
                throw new InvalidOperationException(
                    $"The storage root '{path}' (configuration '{key}') is inside Backup:Root "
                    + $"('{backupRoot}'). Backup retention deletes files in its own root, so no storage "
                    + "root may be nested under it. Point Backup:Root at a directory of its own.");
            }
        }
    }
}
