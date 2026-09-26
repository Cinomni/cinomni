namespace Cinomni.Identity.Application;

/// <summary>Canonical form of a username: trimmed and lower-cased for case-insensitive matching.</summary>
internal static class Usernames
{
    public static string Normalize(string username) =>
        username.Trim().ToLowerInvariant();
}
