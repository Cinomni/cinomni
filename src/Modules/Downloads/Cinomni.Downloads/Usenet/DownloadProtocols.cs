namespace Cinomni.Downloads.Usenet;

/// <summary>The transport a download task uses. Stored as text so a new one is an append, not a renumber.</summary>
public static class DownloadProtocols
{
    public const string Torrent = "Torrent";
    public const string Usenet = "Usenet";

    public static bool IsUsenet(string? protocol) =>
        string.Equals(protocol, Usenet, StringComparison.Ordinal);
}
