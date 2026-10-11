namespace Cinomni.Subtitles.Files;

/// <summary>
/// The name of a synchronized copy: the original's name with <c>.synced</c> before the extension, so
/// <c>Movie.en.hi.srt</c> becomes <c>Movie.en.hi.synced.srt</c>. It still starts with the video's stem,
/// which is what makes Import move it along with the video and <see cref="SidecarRelocation"/> carry its
/// row across, exactly as for the original.
/// </summary>
public static class SyncedSidecar
{
    private const string Marker = ".synced";

    public static string NameFor(string originalPath)
    {
        var name = Path.GetFileName(originalPath);
        return Path.GetFileNameWithoutExtension(name) + Marker + Path.GetExtension(name);
    }

    public static string PathFor(string originalPath) =>
        Path.Combine(Path.GetDirectoryName(originalPath) ?? string.Empty, NameFor(originalPath));

    public static bool IsSyncedName(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).EndsWith(Marker, StringComparison.Ordinal);
}
