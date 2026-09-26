using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cinomni.Kernel.Results;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Backup;

/// <summary>A dump and the manifest that describes it, both present and both named for the same instant.</summary>
public sealed record BackupSet(
    string Stamp,
    DateTimeOffset CreatedAt,
    string DumpPath,
    string ManifestPath,
    long DumpSizeBytes);

/// <summary>
/// Owns the backup directory: what a backup is called, which files count as one, and which ones age out.
/// <para>
/// Retention is the only code in the product that removes data nobody asked it to remove, so it is
/// deliberately narrow. It never recurses, never follows a symbolic link, never accepts a path from a
/// caller, and only ever considers a name that matches a closed pattern anchored at both ends. Anything
/// else in the directory — an operator's own copy, a nested folder, a README — is invisible to it.
/// </para>
/// <para>
/// A backup is published by <b>renaming</b> a finished <c>.partial</c> file into place, so an interrupted
/// run leaves an artefact that is not a backup and is never mistaken for one. The manifest is written
/// last, which makes "dump plus manifest" the definition of a complete set.
/// </para>
/// </summary>
public sealed partial class BackupStore(BackupOptions options, ILogger<BackupStore> logger)
{
    /// <summary>Prefix and extensions are a closed vocabulary; nothing else is ever created or removed.</summary>
    private const string Prefix = "cinomni-";
    private const string DumpExtension = ".dump";
    private const string ManifestExtension = ".manifest.json";
    private const string PartialExtension = ".dump.partial";

    /// <summary>The stamp format, sortable and time-zone free.</summary>
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>
    /// How long an incomplete artefact survives before retention removes it. Long enough that a dump
    /// still being written by a slow run is never touched, short enough that a crashed run does not
    /// leave a copy of the database on disk for ever.
    /// </summary>
    private static readonly TimeSpan IncompleteGrace = TimeSpan.FromDays(1);

    public string Root => options.Root;

    /// <summary>Reported when the backup root cannot be created, or belongs to somebody else.</summary>
    public const string RootUnwritableCode = "backup.root_unwritable";

    /// <summary>
    /// Creates the backup directory if it is absent, and keeps it to this account on Unix.
    /// <para>
    /// A failure here is an operational state and not an exception: a bind-mounted directory owned by
    /// root is the ordinary way this goes wrong, and the run has to report it the way it reports a
    /// missing tool — as a result the journal keeps and the CLI prints — rather than as a stack trace.
    /// </para>
    /// </summary>
    public Result EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(options.Root);

            if (!OperatingSystem.IsWindows())
            {
                // The directory listing alone tells an attacker when the installation was last backed up.
                File.SetUnixFileMode(
                    options.Root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return Result.Success();
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            logger.LogWarning(exception, "The backup root {BackupRoot} could not be prepared.", options.Root);

            return Result.Failure(new Error(
                RootUnwritableCode,
                $"The backup root '{options.Root}' (configuration 'Backup:Root') could not be created or "
                + "is not writable by this process. Check that the volume is mounted and owned by the "
                + "account the application runs as."));
        }
    }

    /// <summary>The filesystem failures a backup run reports rather than crashes on.</summary>
    public static bool IsFilesystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    public static string StampFor(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture);

    public string DumpPathFor(string stamp) => Path.Combine(options.Root, Prefix + stamp + DumpExtension);

    public string PartialPathFor(string stamp) => Path.Combine(options.Root, Prefix + stamp + PartialExtension);

    public string ManifestPathFor(string stamp) => Path.Combine(options.Root, Prefix + stamp + ManifestExtension);

    /// <summary>
    /// Resolves a backup by <b>name</b> — never by path. The name must match the closed pattern, which
    /// means a caller cannot address anything outside the root even by accident. Returns <c>null</c>
    /// when the name is not one this store issues or when the set is not complete.
    /// </summary>
    public BackupSet? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Accept the stamp on its own, the dump name, or the manifest name — all three are things an
        // operator reasonably has in front of them.
        var candidate = Path.GetFileName(name.Trim());
        var stamp = candidate;

        if (candidate.EndsWith(ManifestExtension, StringComparison.Ordinal))
        {
            stamp = candidate[..^ManifestExtension.Length];
        }
        else if (candidate.EndsWith(DumpExtension, StringComparison.Ordinal))
        {
            stamp = candidate[..^DumpExtension.Length];
        }

        if (stamp.StartsWith(Prefix, StringComparison.Ordinal))
        {
            stamp = stamp[Prefix.Length..];
        }

        if (!StampPattern().IsMatch(stamp))
        {
            return null;
        }

        return List().FirstOrDefault(set => string.Equals(set.Stamp, stamp, StringComparison.Ordinal));
    }

    /// <summary>Every complete backup set in the root, newest first. An unpaired file is not a backup.</summary>
    public IReadOnlyList<BackupSet> List()
    {
        if (!Directory.Exists(options.Root))
        {
            return [];
        }

        var dumps = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        var manifests = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateOwnedFiles())
        {
            if (TryReadStamp(file.Name, DumpExtension, out var dumpStamp))
            {
                dumps[dumpStamp] = file;
            }
            else if (TryReadStamp(file.Name, ManifestExtension, out var manifestStamp))
            {
                manifests.Add(manifestStamp);
            }
        }

        return
        [
            .. dumps
                .Where(entry => manifests.Contains(entry.Key))
                .Select(entry => new BackupSet(
                    entry.Key,
                    ParseStamp(entry.Key),
                    entry.Value.FullName,
                    ManifestPathFor(entry.Key),
                    entry.Value.Length))
                .OrderByDescending(set => set.Stamp, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Keeps the newest <paramref name="keepCount"/> complete sets and removes the rest, then sweeps
    /// incomplete artefacts older than the grace period. Returns how many files were removed.
    /// <para>
    /// Called only after a successful run, so a broken backup never costs the operator a good one, and
    /// the count is validated at composition, so this can never be asked to keep zero.
    /// </para>
    /// </summary>
    public int ApplyRetention(int keepCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepCount, 1);

        var removed = 0;

        foreach (var set in List().Skip(keepCount))
        {
            removed += Remove(set.DumpPath);
            removed += Remove(set.ManifestPath);
            logger.LogInformation("Retention removed backup {Backup}, taken {CreatedAt:O}.", set.Stamp, set.CreatedAt);
        }

        removed += SweepIncomplete(now);
        return removed;
    }

    /// <summary>Publishes a finished dump by renaming it into place — the moment it becomes visible.</summary>
    public static void Publish(string partialPath, string finalPath)
    {
        File.Move(partialPath, finalPath, overwrite: false);
        RestrictPermissions(finalPath);
    }

    /// <summary>
    /// Makes a file readable and writable by this account only (0600 on Unix). A dump is a credential
    /// store; the manifest gets the same treatment so a partially-hardened directory is impossible.
    /// </summary>
    public static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Lower-case hexadecimal SHA-256 of a file, streamed so a large dump is never held in memory.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Removes incomplete artefacts — a crashed run's <c>.partial</c>, or a dump with no manifest.
    /// <para>
    /// Public because a run calls it <b>before</b> it writes, not only after it succeeds. Retention
    /// proper is gated on a successful run so a broken backup never costs a good one, but that same gate
    /// would deadlock an installation whose disk is full: every failed attempt leaves a partial, each
    /// attempt gets its own name, and the sweep that would reclaim them needs the success that the
    /// partials are preventing. An artefact older than the grace period is abandoned by definition — no
    /// live run can be writing to it — so sweeping first is safe and breaks exactly that loop.
    /// </para>
    /// </summary>
    public int SweepIncomplete(DateTimeOffset now)
    {
        var cutoff = now - IncompleteGrace;
        var complete = List().Select(set => set.Stamp).ToHashSet(StringComparer.Ordinal);
        var removed = 0;

        foreach (var file in EnumerateOwnedFiles())
        {
            if (file.LastWriteTimeUtc >= cutoff.UtcDateTime)
            {
                continue;
            }

            var isIncomplete =
                (TryReadStamp(file.Name, PartialExtension, out var partialStamp) && !complete.Contains(partialStamp))
                || (TryReadStamp(file.Name, DumpExtension, out var dumpStamp) && !complete.Contains(dumpStamp))
                || (TryReadStamp(file.Name, ManifestExtension, out var manifestStamp) && !complete.Contains(manifestStamp));

            if (!isIncomplete)
            {
                continue;
            }

            removed += Remove(file.FullName);
            logger.LogInformation("Retention removed the incomplete backup artefact {File}.", file.Name);
        }

        return removed;
    }

    /// <summary>
    /// Every regular file directly in the root whose name this store issues. Top level only, no symbolic
    /// links, no directories: the two ways a delete could ever leave the root are both closed here rather
    /// than at each call site.
    /// </summary>
    private IEnumerable<FileInfo> EnumerateOwnedFiles()
    {
        var directory = new DirectoryInfo(options.Root);
        if (!directory.Exists)
        {
            yield break;
        }

        foreach (var file in directory.EnumerateFiles(Prefix + "*", SearchOption.TopDirectoryOnly))
        {
            // A symbolic link named like a backup would let a delete reach outside the root.
            if (file.LinkTarget is not null)
            {
                continue;
            }

            yield return file;
        }
    }

    private int Remove(string path)
    {
        // Belt and braces: only ever a file this store named, and only ever directly in its own root.
        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent is null || !parent.Equals(options.Root, comparison) || !IsOwnedName(name))
        {
            logger.LogWarning("Refused to remove '{Path}': it is not a backup file in the backup root.", path);
            return 0;
        }

        try
        {
            File.Delete(path);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove the backup file {File}.", name);
            return 0;
        }
    }

    private static bool IsOwnedName(string name) =>
        TryReadStamp(name, DumpExtension, out _)
        || TryReadStamp(name, ManifestExtension, out _)
        || TryReadStamp(name, PartialExtension, out _);

    private static bool TryReadStamp(string fileName, string extension, out string stamp)
    {
        stamp = string.Empty;

        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(extension, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = fileName[Prefix.Length..^extension.Length];
        if (!StampPattern().IsMatch(candidate))
        {
            return false;
        }

        stamp = candidate;
        return true;
    }

    private static DateTimeOffset ParseStamp(string stamp) =>
        DateTimeOffset.TryParseExact(
            stamp,
            StampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>The closed name pattern: exactly a UTC stamp, anchored at both ends.</summary>
    [GeneratedRegex(@"^\d{8}T\d{6}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex StampPattern();
}
