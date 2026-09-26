namespace Cinomni.Subtitles.Files;

/// <summary>
/// Where a sidecar subtitle ends up when the video it belongs to is moved.
/// <para>
/// <see cref="LocalSubtitleFileStore"/> writes it as <c>&lt;stem&gt;.&lt;lang&gt;[.forced][.hi].&lt;ext&gt;</c>
/// inside the video's own directory, so the name is derived from the video and not stored
/// independently. That derivation is what has to be replayed here: the part after the stem — the
/// language, the flags and the format — is carried across untouched, because it is the only part this
/// module actually owns.
/// </para>
/// </summary>
public static class SidecarRelocation
{
    /// <summary>
    /// The sidecar's new path, or <see langword="null"/> when it does not belong to the moved video —
    /// it sits in another directory, or its name does not start with the video's stem. Null means
    /// leave the row alone: guessing a path for a file whose relationship to the video we cannot see
    /// would point the library at something that is not there.
    /// </summary>
    public static string? For(string fromVideoPath, string toVideoPath, string sidecarPath)
    {
        var fromDirectory = Path.GetDirectoryName(fromVideoPath);
        var toDirectory = Path.GetDirectoryName(toVideoPath);
        var sidecarDirectory = Path.GetDirectoryName(sidecarPath);
        if (fromDirectory is null || toDirectory is null || sidecarDirectory is null)
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!sidecarDirectory.Equals(fromDirectory, comparison))
        {
            return null;
        }

        var fromStem = Path.GetFileNameWithoutExtension(fromVideoPath);
        var sidecarName = Path.GetFileName(sidecarPath);
        if (fromStem.Length == 0 || !sidecarName.StartsWith(fromStem, comparison))
        {
            return null;
        }

        var suffix = sidecarName[fromStem.Length..];
        return Path.Combine(toDirectory, Path.GetFileNameWithoutExtension(toVideoPath) + suffix);
    }
}
