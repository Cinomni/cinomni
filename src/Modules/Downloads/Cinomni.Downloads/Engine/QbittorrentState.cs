namespace Cinomni.Downloads.Engine;

/// <summary>
/// Maps a qBittorrent state name onto the engine vocabulary <c>DownloadService</c> already understands.
/// Unknown names stay downloading rather than failed: a new client state must not fail a transfer.
/// </summary>
internal static class QbittorrentState
{
    /// <remarks>
    /// qBittorrent 5 renamed its paused states to <c>stoppedDL</c>/<c>stoppedUP</c>; both spellings are
    /// read. <c>queuedDL</c> is the client's own queue — waiting for a slot, which the stall clock must
    /// not count against the download.
    /// </remarks>
    public static (string State, bool IsPaused, bool IsFinished, string? Error, bool IsQueued) Map(string? raw, double progress)
    {
        var state = raw ?? string.Empty;
        var finished = progress >= 1d
            || state is "uploading" or "stalledUP" or "queuedUP" or "forcedUP" or "pausedUP" or "stoppedUP" or "checkingUP";
        return state switch
        {
            "metaDL" => ("downloading_metadata", false, false, null, false),
            "checkingDL" or "checkingUP" or "checkingResumeData" or "moving" => ("checking_files", false, finished, null, false),
            "allocating" => ("allocating", false, false, null, false),
            "pausedDL" or "stoppedDL" => ("downloading", true, false, null, false),
            "queuedDL" => ("downloading", false, finished, null, true),
            "pausedUP" or "stoppedUP" => ("seeding", true, true, null, false),
            "uploading" or "stalledUP" or "queuedUP" or "forcedUP" => ("seeding", false, true, null, false),
            "error" => ("error", false, false, "qbittorrent-error", false),
            "missingFiles" => ("error", false, false, "qbittorrent-missing-files", false),
            _ => ("downloading", false, finished, null, false),
        };
    }
}
