namespace Cinomni.Library.Persistence;

/// <summary>
/// Column-width guard. Paths and codec names originate from imported content and the ffprobe output;
/// truncating before persist keeps one oversized value from overflowing a column and rolling back the
/// whole atomic write.
/// </summary>
internal static class Text
{
    public static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
