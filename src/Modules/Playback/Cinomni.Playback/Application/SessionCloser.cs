using Cinomni.Kernel.Messaging;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;

namespace Cinomni.Playback.Application;

/// <summary>
/// Ends a session, whoever asks: the viewer pressing stop, or the sweep finding that nothing has asked
/// for the stream in a while. Both mean the same thing — nobody is watching — so both stop the
/// transcode, reclaim its output and close the session the same way.
/// </summary>
public sealed class SessionCloser(SessionUpdates updates, ActiveTranscodes transcodes)
{
    /// <summary>
    /// Stops the process and reclaims the output first — whatever the row says: the watched threshold
    /// completes a session while the credits are still streaming, so a completed session can still have
    /// FFmpeg running — then records the close. A close whose save never lands (the host died in
    /// between, or every retry lost its race) leaves an open row nothing tracks, which the sweep's
    /// reconciliation finds and closes.
    /// </summary>
    public Task CloseAsync(Guid sessionId, PlaybackEndReason reason, CancellationToken cancellationToken = default) =>
        transcodes.EndForCloseAsync(
            sessionId, () => updates.ApplyAsync(sessionId, session => Close(session, reason), cancellationToken));

    /// <summary>
    /// The row half of a close: jobs Cleaned, session Completed with <paramref name="reason"/>, and
    /// <c>PlaybackCompleted</c> once. A session that had already ended keeps the reason it ended with.
    /// </summary>
    internal static IReadOnlyList<IDomainEvent>? Close(PlaybackSession session, PlaybackEndReason reason)
    {
        var now = DateTimeOffset.UtcNow;
        var released = session.ReleaseTranscode(now);
        var stopped = session.Stop(now, reason);

        return stopped
            ? [new PlaybackCompleted(session.Id, session.UserId, session.AssetId)]
            : released ? [] : null;
    }
}
