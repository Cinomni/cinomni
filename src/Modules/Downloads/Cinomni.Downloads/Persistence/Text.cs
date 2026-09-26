namespace Cinomni.Downloads.Persistence;

/// <summary>
/// Column-width guard. Names, paths and error messages originate from torrents and the sidecar
/// (arbitrary length); truncating before persist keeps one oversized value from overflowing a
/// column and rolling back the whole atomic write.
/// </summary>
internal static class Text
{
    public static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Reduces a torrent name to <b>one</b> directory segment that is safe to compose a path from.
    /// <para>
    /// A torrent name is swarm-supplied data and reaches the filesystem twice: the sidecar stages the
    /// content under it, and this module composes <c>save path + name</c> as the root Import is told
    /// to scan. A name carrying separators, a parent-directory token or a device prefix would point
    /// that scan at an arbitrary directory, from which anything passing the import policy is hardlinked
    /// into the household's library under the operator's own catalogue title.
    /// </para>
    /// <para>
    /// Everything outside a single ordinary segment becomes an underscore rather than being dropped,
    /// so two names cannot collapse into one, and a name that survives untouched — which is every
    /// ordinary release — composes exactly the path it always did.
    /// </para>
    /// </summary>
    public static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = new string([.. value.Select(Safe)]).Trim();

        // Windows drops trailing dots and spaces, so a name ending in them names a different file
        // there than the one recorded here.
        cleaned = cleaned.TrimEnd('.', ' ');

        // "." and ".." survive the character filter intact and are exactly the two segments that must
        // never be composed into a path.
        return cleaned.Trim('.').Length == 0 ? string.Empty : cleaned;
    }

    /// <summary>Separators, drive/device punctuation and control characters are not part of a name.</summary>
    private static char Safe(char character) =>
        character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(character)
            ? '_'
            : character;
}
