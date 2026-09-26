namespace Cinomni.Playback.Persistence;

/// <summary>Column-width guard: truncates before persist so one oversized value cannot roll back the write.</summary>
internal static class Text
{
    public static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Keeps the end instead of the start — for a process's output, where the reason for a failure is
    /// the last thing it wrote.
    /// </summary>
    public static string Tail(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[^maxLength..];
}
