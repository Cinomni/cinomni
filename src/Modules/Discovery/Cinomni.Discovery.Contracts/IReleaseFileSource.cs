namespace Cinomni.Discovery.Contracts;

/// <summary>What <see cref="IReleaseFileSource"/> did with a download link.</summary>
public enum ReleaseFileOutcome
{
    /// <summary>
    /// No indexer that signs in owns this link. The caller fetches it as it always has — an anonymous
    /// request — which is right for every public tracker and every magnet.
    /// </summary>
    NotHandled = 0,

    /// <summary>The link was fetched with the owning indexer's session; the .torrent file is attached.</summary>
    TorrentFile = 1,

    /// <summary>The owning indexer answered with a magnet link instead of a file.</summary>
    Magnet = 2,
}

/// <summary>
/// The release file behind a download link. Carries the file itself, never the session that fetched it:
/// the cookies stay in Discovery, which keeps them encrypted, and nothing downstream can replay them.
/// </summary>
public sealed record ReleaseFile(ReleaseFileOutcome Outcome, byte[]? TorrentFile = null, string? Magnet = null)
{
    public static ReleaseFile NotHandled { get; } = new(ReleaseFileOutcome.NotHandled);
}

/// <summary>
/// Fetches a release file on behalf of the module that downloads it, when the link belongs to an
/// indexer that signs in. A private tracker serves its .torrent only to a signed-in member, and only
/// Discovery holds the session to do that as.
/// <para>
/// Called once per download, at the moment the release is handed to the download client — never while
/// searching. Many private trackers record the fetch of a .torrent as a download by that account, so
/// fetching ahead of the decision would charge the member for releases nobody chose.
/// </para>
/// </summary>
public interface IReleaseFileSource
{
    /// <summary>
    /// The file behind <paramref name="downloadUrl"/>, or <see cref="ReleaseFile.NotHandled"/> when no
    /// indexer that signs in owns it. A link that is owned but cannot be fetched throws — the caller's
    /// retry is what turns a transient failure into a delay — and the exception never quotes the link.
    /// </summary>
    Task<ReleaseFile> FetchAsync(string downloadUrl, CancellationToken cancellationToken = default);
}
