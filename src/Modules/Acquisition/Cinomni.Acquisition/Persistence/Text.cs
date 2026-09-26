namespace Cinomni.Acquisition.Persistence;

/// <summary>
/// Column-width guard. Release guids, urls and failure reasons originate from indexers (hostile,
/// arbitrary length); truncating before persist keeps one oversized value from overflowing a
/// column and rolling back the whole atomic write.
/// </summary>
internal static class Text
{
    public static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
