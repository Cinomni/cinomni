using System.Buffers;

namespace Cinomni.Import.Files;

/// <summary>
/// Confines every import path to a registered root (path-traversal defense). Content
/// arrives from a download (hostile data), so a path is only accepted once its canonical form is
/// resolved (no <c>..</c>, no symlink escape) and proven to sit <b>inside</b> an allowed root. This
/// is the decision point; the actual disk touch (<see cref="IImportFileSystem"/>) happens only on a
/// path this guard has cleared.
/// </summary>
public static class PathGuard
{
    // Windows device/UNC prefixes and reserved names we refuse outright.
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// The punctuation <see cref="SanitizeName"/> replaces on every platform; every character below
    /// U+0020 is replaced as well. The set is deliberately the Windows one —
    /// <c>&lt; &gt; : " / \ | ? *</c> plus the C0 controls — even though the Host runs on Linux: the
    /// library tree is served to Windows and macOS clients over SMB, where such a name is
    /// unrepresentable (the file cannot be read, renamed or backed up from there), and a raw CR/LF
    /// inside a name corrupts any line-oriented tool or log line that echoes the path.
    /// <para>
    /// It is a fixed set rather than <c>Path.GetInvalidFileNameChars()</c> on purpose. That call is
    /// platform-dependent — Unix reports only <c>\0</c> and <c>/</c>, Windows 41 characters — which
    /// made the sanitiser a near no-op on the one platform Cinomni ships, for names taken verbatim
    /// from release titles and provider metadata, i.e. hostile input. Every other rule here is
    /// already platform-independent, so the result no longer depends on where the process runs.
    /// </para>
    /// </summary>
    private static readonly SearchValues<char> InvalidNamePunctuation = SearchValues.Create("<>:\"/\\|?*");

    /// <summary>
    /// Resolves <paramref name="candidate"/> and returns its canonical absolute path only if it lies
    /// within <paramref name="root"/>; otherwise null (rejected). Both are canonicalised so that
    /// <c>..</c> traversal and mixed separators cannot escape the root, and then checked again with every
    /// symbolic link along the part of each path that exists resolved: a link inside the root that
    /// points out of it — which a torrent can create in staging — is an escape the text of the path
    /// does not show. The lexical form is what is returned, so a root reached through a link of its own
    /// keeps working.
    /// </summary>
    public static string? Confine(string root, string candidate)
    {
        if (ConfineLexically(root, candidate) is not { } lexical)
        {
            return null;
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return lexical.Equals(canonicalRoot, Comparison) || ResolvesInside(canonicalRoot, lexical, Comparison)
            ? lexical
            : null;
    }

    /// <summary>
    /// <see cref="Confine"/> without looking at the disk: the text of the path alone — no <c>..</c>, no
    /// device or UNC path, inside the root. Not a confinement on its own, since a link inside the root can
    /// lead out of it; it is the cheap first question for a caller about to decide, for many paths at
    /// once, which few need the full check.
    /// </summary>
    public static string? ConfineLexically(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (HasDangerousComponent(candidate))
        {
            return null;
        }

        string canonicalRoot;
        string canonicalCandidate;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            canonicalCandidate = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // The candidate must be the root itself or a descendant of it (root + separator).
        if (canonicalCandidate.Equals(canonicalRoot, Comparison))
        {
            return canonicalCandidate;
        }

        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(prefix, Comparison) ? canonicalCandidate : null;
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>How many links one path may pass through before it is treated as a loop.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Whether <paramref name="candidate"/>, with its links resolved, still lies inside
    /// <paramref name="root"/> with its own links resolved. A link that cannot be followed (a loop, an
    /// unreadable directory) is refused: its target is unknown, so so is whether it escapes. A dangling
    /// one is judged by where it points, which is where a write through it would land.
    /// </summary>
    private static bool ResolvesInside(string root, string candidate, StringComparison comparison)
    {
        var realRoot = RealPath(root);
        var realCandidate = RealPath(candidate);
        if (realRoot is null || realCandidate is null)
        {
            return false;
        }

        realRoot = Path.TrimEndingDirectorySeparator(realRoot);
        realCandidate = Path.TrimEndingDirectorySeparator(realCandidate);
        return realCandidate.Equals(realRoot, comparison)
            || realCandidate.StartsWith(realRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// <paramref name="fullPath"/> with every symbolic link along its existing part replaced by where it
    /// points; the part that does not exist yet (a destination about to be created) is kept as written.
    /// Null when a link cannot be followed.
    /// </summary>
    private static string? RealPath(string fullPath)
    {
        try
        {
            var hops = 0;
            var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
            var pending = new LinkedList<string>(Segments(fullPath[pathRoot.Length..]));
            var current = pathRoot;

            while (pending.First is { } first)
            {
                var segment = first.Value;
                pending.RemoveFirst();

                // Applied to the path resolved so far, as the kernel does — after the links before it
                // were followed, never by collapsing text: `link/..` is the parent of where the link
                // leads, not the directory holding the link.
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? current;
                    continue;
                }

                var next = Path.Combine(current, segment);
                FileSystemInfo entry = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (entry.LinkTarget is { } target)
                {
                    if (++hops > MaxLinkHops)
                    {
                        return null;
                    }

                    // The target's own segments are walked next, from its root when it is absolute and
                    // from the directory holding the link when it is relative, followed by what is left.
                    var targetRoot = Path.GetPathRoot(target) ?? string.Empty;
                    if (targetRoot.Length > 0)
                    {
                        current = targetRoot;
                    }

                    var segments = Segments(target[targetRoot.Length..]).ToList();
                    for (var i = segments.Count - 1; i >= 0; i--)
                    {
                        pending.AddFirst(segments[i]);
                    }

                    continue;
                }

                if (!entry.Exists)
                {
                    // Nothing here yet, so nothing below it can be a link: the rest is as written.
                    return Path.GetFullPath(Path.Combine([next, .. pending]));
                }

                current = next;
            }

            return Path.GetFullPath(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Segments(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(segment => segment.Length > 0);

    /// <summary>True if <paramref name="candidate"/> is confined within <paramref name="root"/>.</summary>
    public static bool IsWithin(string root, string candidate) => Confine(root, candidate) is not null;

    /// <summary>
    /// True if <paramref name="candidate"/> is inside <paramref name="root"/> and is not the root itself.
    /// What a download is scanned from must be its own folder: the staging root is every download at
    /// once, and scanning it takes another download's files for this one.
    /// </summary>
    public static bool IsStrictlyWithin(string root, string candidate)
    {
        var confined = Confine(root, candidate);
        if (confined is null)
        {
            return false;
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.TrimEndingDirectorySeparator(confined).Equals(canonicalRoot, comparison))
        {
            return false;
        }

        // Nor the root reached through a link: a download folder that is a link to `..` is the staging
        // root by another name, and scanning it is scanning every download.
        return RealPath(confined) is { } realCandidate
            && RealPath(canonicalRoot) is { } realRoot
            && !Path.TrimEndingDirectorySeparator(realCandidate).Equals(Path.TrimEndingDirectorySeparator(realRoot), comparison);
    }

    /// <summary>Sanitises a single file/directory name to valid, bounded characters (organiser rename).</summary>
    public static string SanitizeName(string name, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var cleaned = new string(name.Select(static c => IsInvalidNameChar(c) ? '_' : c).ToArray()).Trim();

        // Strip trailing dots/spaces (Windows drops them) and cap the length.
        cleaned = cleaned.TrimEnd('.', ' ');
        if (cleaned.Length == 0)
        {
            return "unnamed";
        }

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (Array.Exists(ReservedNames, r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase)))
        {
            cleaned = "_" + cleaned;
        }

        // Trailing dots and spaces are dropped by Windows, and truncation can expose a fresh one.
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd('.', ' ');
    }

    /// <summary>
    /// True for a character no library name may carry: the fixed punctuation deny set or any C0
    /// control (U+0000-U+001F). See <see cref="InvalidNamePunctuation"/> for why the set is fixed.
    /// </summary>
    private static bool IsInvalidNameChar(char c) => c < ' ' || InvalidNamePunctuation.Contains(c);

    private static bool HasDangerousComponent(string candidate)
    {
        // Explicit parent-directory tokens, before canonicalisation collapses them.
        var parts = candidate.Split('/', '\\');
        if (Array.Exists(parts, p => p == ".."))
        {
            return true;
        }

        // Device paths and UNC shares (\\server\share, \\?\, //server) are out of any local root.
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
