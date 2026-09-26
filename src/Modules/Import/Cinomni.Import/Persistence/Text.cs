namespace Cinomni.Import.Persistence;

/// <summary>
/// Column-width guard. Paths, container names and error messages originate from downloaded content
/// and the ffprobe output (arbitrary length); truncating before persist keeps one oversized value
/// from overflowing a column and rolling back the whole atomic write.
/// </summary>
internal static class Text
{
    public static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
