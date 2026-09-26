using Cinomni.Downloads.Contracts;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// Maps between our coarse <see cref="FilePriorityLevel"/> and libtorrent's 0..7 wire scale (where
/// 0 = don't download and 4 = the engine's default). Our enum values are semantic, not the wire
/// numbers, so the conversion is explicit rather than a cast.
/// </summary>
internal static class LibtorrentPriority
{
    public static int ToWire(FilePriorityLevel level) => level switch
    {
        FilePriorityLevel.Skip => 0,
        FilePriorityLevel.Normal => 4,
        FilePriorityLevel.High => 6,
        FilePriorityLevel.Top => 7,
        _ => 4,
    };

    public static FilePriorityLevel FromWire(int priority) => priority switch
    {
        0 => FilePriorityLevel.Skip,
        7 => FilePriorityLevel.Top,
        6 => FilePriorityLevel.High,
        _ => FilePriorityLevel.Normal,
    };
}
