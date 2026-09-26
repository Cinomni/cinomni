namespace Cinomni.Downloads.Engine;

/// <summary>
/// Answers whether the configured torrent engine — the sidecar or an external qBittorrent — can be
/// reached, without doing anything to it. Each engine registration provides one, so the readiness
/// check asks the engine actually in use instead of assuming the sidecar.
/// </summary>
public interface IDownloadEngineProbe
{
    /// <summary>
    /// Whether the engine answers within <paramref name="timeout"/>. Never throws: an unreachable
    /// engine is an answer, not an error, and an exception message would name its address.
    /// </summary>
    Task<bool> IsReachableAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
