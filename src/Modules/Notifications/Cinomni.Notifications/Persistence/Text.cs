namespace Cinomni.Notifications.Persistence;

/// <summary>Column-width guard: truncates before persist so one oversized value cannot roll back the write.</summary>
internal static class Text
{
    public static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
