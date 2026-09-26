using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;

namespace Cinomni.Playback.Contracts;

/// <summary>
/// Write surface of the Playback module. Requesting playback plans the delivery (explainable) and
/// opens a session; progress reports advance it and persist the resume position; stopping ends it.
/// All are keyed by the session/(user,asset) so they are safe to retry.
/// </summary>
public interface IPlaybackSessionCommands
{
    /// <summary>
    /// Plans how to deliver <paramref name="assetId"/> to a client with the given capabilities
    /// (DirectPlay/Remux/Transcode, explained), opens a session and returns the ticket. Fails if the
    /// asset is unknown, or if <paramref name="preferences"/> names a track or quality the file does
    /// not have.
    /// </summary>
    Task<Result<PlaybackTicket>> RequestPlaybackAsync(
        Viewer viewer,
        Guid assetId,
        ClientCapability capability,
        PlaybackPreferences? preferences = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports the client's position (and pause state) for a session: advances the finite-state
    /// machine, persists the resume position with thresholds, and completes the session past 90%.
    /// No-op for a session that is not the viewer's or already terminal. Access to the title is checked
    /// again on every report: a viewer who may no longer see it has the session closed instead.
    /// </summary>
    Task ReportProgressAsync(
        Viewer viewer,
        PlaybackSessionId sessionId,
        long positionTicks,
        long durationTicks,
        bool isPaused,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the viewer switched subtitles in the player (null: off), so the next session of the
    /// title starts with them. False — and nothing changes — when the session is not the viewer's, or the
    /// index names no subtitle stream of the file.
    /// </summary>
    Task<bool> ChooseSubtitleAsync(
        Viewer viewer,
        PlaybackSessionId sessionId,
        int? subtitleStreamIndex,
        CancellationToken cancellationToken = default);

    /// <summary>Ends a session (client stopped). No-op if it is not the user's or already terminal.</summary>
    Task StopPlaybackAsync(Guid userId, PlaybackSessionId sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Read model of playback sessions and resume progress.</summary>
public interface IPlaybackQuery
{
    /// <summary>
    /// A session, scoped to the user who owns it — another account's session reads as missing, the same
    /// answer the stream endpoints give. Installations are multi-account, so a session id is not a capability.
    /// </summary>
    Task<PlaybackSessionDetail?> GetSessionAsync(
        Guid userId,
        PlaybackSessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<PlaybackProgressView?> GetProgressAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume state for many assets in one round trip. The per-episode UI needs the watched flag of a
    /// whole season to render one page; asking per episode is an N+1 on the view that renders most
    /// often. Assets with no progress are simply absent from the result — an empty request returns
    /// an empty list.
    /// </summary>
    Task<IReadOnlyList<PlaybackProgressView>> GetProgressForAssetsAsync(
        Guid userId,
        IReadOnlyList<Guid> assetIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// "Continue watching": the viewer's started, unfinished items, most recently watched first, at most
    /// <paramref name="limit"/> of them. A row whose title the viewer may no longer see is left out, as is a
    /// row written before progress was correlated with its work — there is nothing to check access against.
    /// </summary>
    Task<IReadOnlyList<PlaybackProgressView>> GetInProgressAsync(
        Viewer viewer,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The next episode of a series the user should watch: the first one in season/episode order that
    /// has a playable asset and is not yet watched. Returns null for a movie, for an unknown work, and
    /// when every playable episode has been watched — and for a work the viewer may not see, exactly as
    /// for one that does not exist, so the answer is no oracle for a hidden title's episodes.
    /// </summary>
    Task<NextUpView?> GetNextUpAsync(Viewer viewer, Guid workId, CancellationToken cancellationToken = default);
}
