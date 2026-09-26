namespace Cinomni.Downloads.Engine;

/// <summary>
/// How to reach an external qBittorrent Web API. Empty <see cref="BaseAddress"/> means the client is
/// not configured. The password is a secret: it is sent as a form field and never logged.
/// </summary>
public sealed class QbittorrentOptions
{
    public string BaseAddress { get; set; } = string.Empty;

    public string Username { get; set; } = "admin";

    public string Password { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often a status subscription polls. The sidecar pushes; this client cannot.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
