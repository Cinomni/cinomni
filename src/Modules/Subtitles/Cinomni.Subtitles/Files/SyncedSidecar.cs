using System.Text.RegularExpressions;

namespace Cinomni.Subtitles.Files;

/// <summary>
/// The name of a synchronized copy: the original's name with <c>.synced-&lt;token&gt;</c> before the
/// extension, so <c>Movie.en.hi.srt</c> becomes <c>Movie.en.hi.synced-3f9c2a7e01b4.srt</c>. It still
/// starts with the video's stem, which is what makes Import move it along with the video and
/// <see cref="SidecarRelocation"/> carry its row across, exactly as for the original.
/// <para>
/// The token is new for every pass. A copy therefore never lands on a name that already exists — not a
/// file the household keeps under a <c>.synced</c> name of its own, and not the copy being played while
/// a second pass runs — and only a name of exactly this shape is ever treated as this module's to delete.
/// </para>
/// </summary>
public static partial class SyncedSidecar
{
    private const string Marker = ".synced-";

    /// <summary>Twelve lowercase hex digits: enough that two passes never meet, short enough to read.</summary>
    [GeneratedRegex(@"\.synced-[0-9a-f]{12}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex SyncedSuffix();

    public static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    public static string NameFor(string originalPath, string token)
    {
        var name = Path.GetFileName(originalPath);
        return Path.GetFileNameWithoutExtension(name) + Marker + token + Path.GetExtension(name);
    }

    public static string PathFor(string originalPath, string token) =>
        Path.Combine(Path.GetDirectoryName(originalPath) ?? string.Empty, NameFor(originalPath, token));

    /// <summary>Whether a file name is one this module wrote, and so one it may remove.</summary>
    public static bool IsSyncedName(string fileName) =>
        SyncedSuffix().IsMatch(Path.GetFileNameWithoutExtension(fileName));
}
