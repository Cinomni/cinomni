using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Playback.Application;

/// <summary>A resolved file to serve: its absolute path and content type.</summary>
public sealed record ServableFile(string Path, string ContentType);

/// <summary>
/// Resolves the concrete file to stream for a session — the media file itself for Direct Play, or an
/// HLS manifest/segment for a transcode. Every returned path is confined: the media path comes from
/// Library (already validated at import), and an HLS file name is accepted only if it is a plain,
/// safe file name that resolves <b>inside</b> the session's own output directory (no traversal).
/// <para>
/// Access is re-checked on every file, not just when the session opened: <b>revocation is enforced on
/// read, not by session teardown</b>. Taking a collection away stops an in-flight stream at the next
/// segment (~4 s of HLS) or the next range request, without Catalog having to reach into Playback to
/// kill anything. A session with no work recorded — one that predates the column — serves nothing.
/// </para>
/// <para>
/// Every HLS file served is also a sign of life: it keeps the session's transcode from being taken
/// for abandoned (<see cref="ActiveTranscodes.Touch"/>).
/// </para>
/// </summary>
public sealed class PlaybackStreamer(PlaybackDbContext dbContext, IContentAccess access, ActiveTranscodes transcodes)
{
    public async Task<ServableFile?> ResolveDirectFileAsync(Viewer viewer, PlaybackSessionId sessionId, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId.Value, cancellationToken);
        if (session is null || session.UserId != viewer.UserId || session.Method != PlaybackMethod.DirectPlay)
        {
            return null;
        }

        if (!await MaySeeAsync(viewer, session, cancellationToken))
        {
            return null;
        }

        return File.Exists(session.FullPath)
            ? new ServableFile(session.FullPath, ContentTypeForMedia(session.FullPath))
            : null;
    }

    public async Task<ServableFile?> ResolveHlsFileAsync(
        Viewer viewer,
        PlaybackSessionId sessionId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeHlsName(fileName))
        {
            return null;
        }

        var session = await dbContext.Sessions
            .AsNoTracking()
            .Include(s => s.TranscodeJobs)
            .FirstOrDefaultAsync(s => s.Id == sessionId.Value, cancellationToken);
        var outputDirectory = session?.TranscodeJobs.FirstOrDefault()?.OutputPath;
        if (session is null || session.UserId != viewer.UserId || outputDirectory is null)
        {
            return null;
        }

        if (!await MaySeeAsync(viewer, session, cancellationToken))
        {
            return null;
        }

        // Confine: the resolved path must stay inside the session's output directory (no escape).
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(candidate))
        {
            return null;
        }

        transcodes.Touch(session.Id);
        return new ServableFile(candidate, ContentTypeForHls(fileName));
    }

    /// <summary>
    /// Whether this viewer may still see what the session is serving. A session opened before the work
    /// was recorded answers false: there is nothing to check it against, and serving is the unsafe default.
    /// </summary>
    private async Task<bool> MaySeeAsync(Viewer viewer, PlaybackSession session, CancellationToken cancellationToken) =>
        session.WorkId is { } workId && await access.CanSeeWorkAsync(viewer, workId, cancellationToken);

    private static bool IsSafeHlsName(string fileName) =>
        !string.IsNullOrEmpty(fileName)
        && fileName.IndexOfAny(['/', '\\']) < 0
        && !fileName.Contains("..", StringComparison.Ordinal)
        && (fileName.EndsWith(".m3u8", StringComparison.Ordinal)
            || fileName.EndsWith(".ts", StringComparison.Ordinal)
            || fileName.EndsWith(".m4s", StringComparison.Ordinal)
            || fileName == FfmpegCommand.InitSegmentName)
        && fileName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static string ContentTypeForHls(string fileName) => Path.GetExtension(fileName) switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".m4s" => "video/iso.segment",
        ".mp4" => "video/mp4",
        _ => "video/mp2t",
    };

    private static string ContentTypeForMedia(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".ts" => "video/mp2t",
        _ => "application/octet-stream",
    };
}
