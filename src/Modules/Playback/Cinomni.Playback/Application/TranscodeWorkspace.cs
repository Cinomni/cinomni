using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Application;

/// <summary>
/// Owns the per-session directories under <see cref="PlaybackOptions.TranscodeRoot"/> — the decoded
/// copy of a library file that just-in-time HLS leaves on disk.
/// <para>
/// A directory is a cache of one stream, not history: it is reclaimed when its session ends or goes
/// silent (<see cref="ActiveTranscodes"/>), and a directory no running transcode owns is reclaimed by
/// the next sweep (<see cref="TranscodeSweep"/>) — one no session row names only when it holds nothing
/// but HLS output (<see cref="HoldsOnlyTranscodeOutput"/>). The retention purge reclaims it again
/// before it deletes the row, as a last backstop.
/// </para>
/// <para>
/// The path is derived from the session id — never from a stored path, which came back from an
/// external encoder — and is then canonicalised and confined under the root. A directory that
/// resolves outside the root, or that is a symbolic link, is refused rather than followed: this code
/// deletes recursively, and following a link out of the root would delete somebody's library.
/// </para>
/// </summary>
public sealed partial class TranscodeWorkspace(PlaybackOptions options, ILogger<TranscodeWorkspace> logger)
{
    /// <summary>What FFmpeg's HLS muxer names its files: the manifest and numbered segments, or a temporary of either.</summary>
    [GeneratedRegex(@"^(manifest\.m3u8|init\.mp4|seg_[0-9]+\.(ts|m4s))(\.tmp)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex OutputFileName();

    /// <summary>
    /// Removes the output directory of one finished session. Idempotent — a missing directory is the
    /// desired state — and never throws: reclaiming disk is best-effort maintenance, and a directory
    /// that cannot be removed must not fail the purge that would have removed the row anyway.
    /// </summary>
    /// <returns><c>true</c> when a directory was actually removed.</returns>
    public bool Remove(Guid sessionId)
    {
        var directory = Confine(sessionId);
        if (directory is null || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            // A symlinked session directory is not ours to follow; deleting the link itself would still
            // be a recursive delete of whatever it points at on some platforms, so refuse outright.
            if (File.ResolveLinkTarget(directory, returnFinalTarget: false) is not null)
            {
                logger.LogWarning(
                    "Refusing to reclaim the transcode directory of session {SessionId}: it is a link, not a "
                    + "directory this module created.",
                    sessionId);
                return false;
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                failure,
                "Could not reclaim the transcode directory of session {SessionId}; it will be retried on the "
                + "next sweep only if the session row is still there.",
                sessionId);
            return false;
        }
    }

    /// <summary>Whether the session has an output directory under the root.</summary>
    public bool HasOutput(Guid sessionId) => Confine(sessionId) is { } directory && Directory.Exists(directory);

    /// <summary>
    /// Whether a session directory holds nothing but what FFmpeg writes there — the manifest and its
    /// segments, perhaps mid-rename — with no subdirectory and no link. The sweep asks before it removes
    /// a directory no session row names: a session id is only a GUID, and a GUID-named directory under a
    /// misconfigured root may well belong to somebody else. False whenever it cannot tell.
    /// </summary>
    public bool HoldsOnlyTranscodeOutput(Guid sessionId)
    {
        if (Confine(sessionId) is not { } directory || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            if (File.ResolveLinkTarget(directory, returnFinalTarget: false) is not null)
            {
                return false;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry) || File.ResolveLinkTarget(entry, returnFinalTarget: false) is not null
                    || !OutputFileName().IsMatch(Path.GetFileName(entry)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The sessions that have an output directory under the root right now: the direct children whose
    /// name is a session id, and nothing else. Anything the root holds that this module did not name —
    /// a file, a directory with some other name — is none of its business and never listed. Never
    /// throws: a root that is missing or unreadable simply has nothing to list.
    /// </summary>
    public IReadOnlyList<Guid> SessionDirectories()
    {
        try
        {
            var root = Path.GetFullPath(options.TranscodeRoot);
            if (!Directory.Exists(root))
            {
                return [];
            }

            var sessions = new List<Guid>();
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (Guid.TryParseExact(Path.GetFileName(directory), "D", out var sessionId))
                {
                    sessions.Add(sessionId);
                }
            }

            return sessions;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(failure, "Could not list the transcode root {Root}.", options.TranscodeRoot);
            return [];
        }
    }

    /// <summary>
    /// The canonical directory for a session, or <c>null</c> when it does not resolve strictly inside
    /// the configured root (a misconfigured root, or a root that is itself a relative path resolving
    /// somewhere unexpected).
    /// </summary>
    private string? Confine(Guid sessionId)
    {
        string root;
        string candidate;

        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.TranscodeRoot));
            candidate = Path.GetFullPath(Path.Combine(root, sessionId.ToString("D")));
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            logger.LogWarning(
                failure, "Transcode root {Root} is not a usable path; nothing was reclaimed.", options.TranscodeRoot);
            return null;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate : null;
    }
}
