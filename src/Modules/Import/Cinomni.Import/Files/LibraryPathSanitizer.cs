using Cinomni.Import.Application;

namespace Cinomni.Import.Files;

/// <summary>
/// The path a file already in the library ought to carry, given the naming rules as they stand today.
/// <para>
/// It exists because <see cref="PathGuard.SanitizeName"/> once built its deny set from
/// <c>Path.GetInvalidFileNameChars()</c>, which answers with two characters on Linux and forty-one on
/// Windows. On the only platform Cinomni ships the sanitiser was therefore very nearly a no-op, and
/// release titles and provider metadata — hostile input — reached disk almost verbatim. The rule is
/// fixed now, but the names an earlier build already wrote are still on disk, and a re-import of that
/// content composes the sanitised path and so no longer finds the file that is actually there.
/// </para>
/// <para>
/// It repairs <b>only</b> what makes a name invalid. It deliberately does not re-apply
/// <see cref="ImportOptions.MaxSegmentLength"/> or re-derive the name from the catalogue: a file whose
/// only sin is being long works perfectly well, and renaming it would be this pass inventing work
/// rather than repairing damage. Every path it returns is confined to the library root, so a stored
/// path that points outside it is reported as nothing to do rather than followed.
/// </para>
/// </summary>
public sealed class LibraryPathSanitizer(ImportOptions options)
{
    /// <summary>
    /// Where <paramref name="currentFullPath"/> should live, or <see langword="null"/> when there is
    /// nothing to do — the name is already clean, the path is the root itself, or it does not confine
    /// to the library at all.
    /// </summary>
    /// <remarks>
    /// Asked of every active path in the library in one pass, so the disk is only consulted for the few
    /// whose names actually need repairing: the name is judged on the text of the path, and only a path
    /// that would be moved — both where it is and where it would go — is then confined with its links
    /// resolved. A library of tens of thousands of clean files costs no disk access at all here.
    /// </remarks>
    public string? SanitisedPathFor(string currentFullPath)
    {
        if (PathGuard.ConfineLexically(options.LibraryRoot, currentFullPath) is not { } confined)
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.LibraryRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (confined.Equals(root, comparison))
        {
            return null;
        }

        var segments = confined[(root.Length + 1)..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        // Every segment, not just the leaf: a series lands two directories deep, and a season folder
        // named from provider text carries the same characters the file name does.
        //
        // The length cap is deliberately lifted (int.MaxValue). SanitizeName's other rules — the
        // forbidden characters, the C0 controls, trailing dots and spaces, the reserved device names —
        // are all about names a filesystem or an SMB client cannot represent, and those are worth
        // repairing. Truncation is not: it would rename files that work.
        var sanitised = Array.ConvertAll(segments, segment => PathGuard.SanitizeName(segment, int.MaxValue));
        if (segments.SequenceEqual(sanitised, StringComparer.Ordinal))
        {
            return null;
        }

        // Only now the disk: a path that is about to be moved must not lead out of the library through a
        // link, where it is and where it goes.
        return PathGuard.Confine(options.LibraryRoot, confined) is not null
            ? PathGuard.Confine(options.LibraryRoot, Path.Combine([root, .. sanitised]))
            : null;
    }
}
