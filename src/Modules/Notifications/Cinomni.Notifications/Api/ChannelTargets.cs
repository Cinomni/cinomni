namespace Cinomni.Notifications.Api;

/// <summary>
/// Masks a channel target for display. A Discord webhook URL carries its token in the path, so it is
/// bearer-equivalent: the API shows where a channel points (scheme + host) and elides the rest, rather
/// than handing the secret back to every browser that opens Settings.
/// </summary>
internal static class ChannelTargets
{
    public static string Mask(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return "…";
        }

        // A bare host with no path holds nothing secret; anything deeper is elided wholesale.
        var path = uri.AbsolutePath;
        return path is "" or "/"
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}/…";
    }
}
