using Cinomni.Downloads.Contracts;

namespace Cinomni.Downloads.Persistence;

/// <summary>
/// One file inside a torrent, with its selected download priority. Populated once the layout is
/// known (a .torrent has it immediately; a magnet after its metadata resolves) and refreshed when
/// priorities change. Keyed by (<see cref="DownloadTaskId"/>, <see cref="Index"/>).
/// </summary>
public sealed class TorrentFile
{
    public Guid DownloadTaskId { get; init; }

    /// <summary>0-based file index within the torrent (matches libtorrent's file order).</summary>
    public int Index { get; init; }

    public required string Path { get; set; }

    public long Size { get; set; }

    public FilePriorityLevel Priority { get; internal set; }
}
