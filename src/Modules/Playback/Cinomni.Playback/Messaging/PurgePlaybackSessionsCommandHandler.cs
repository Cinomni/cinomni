using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Retention;
using Cinomni.Playback.Application;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Diagnostics;
using Cinomni.Playback.Persistence;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Messaging;

/// <summary>
/// Removes playback sessions that are over, taking their transcode jobs with them through the
/// schema's cascade and their on-disk HLS output with them through <see cref="TranscodeWorkspace"/>.
/// <para>
/// Two rules, because a session can stop mattering in two ways. A session that reached
/// <c>Completed</c> or <c>Failed</c> ages out on <see cref="PlaybackOptions.SessionRetention"/>. A
/// session that never reached a terminal state — the client closed the tab and nothing ever told the
/// server — ages out on the same window with a floor of <see cref="AbandonedGrace"/>: no live
/// playback goes a whole day without a progress report, and without this rule those rows and their
/// segment directories are the ones that accumulate for ever.
/// </para>
/// <para>
/// The directory is reclaimed before the row is deleted, and never after: the row is the only thing
/// that names the directory. An interruption in between leaves the row for the next sweep, which is
/// why the reclaim is idempotent. The transcode gauge is released in the same place and for the same
/// reason — an abandoned session is a session nothing else will ever tell this node has ended.
/// </para>
/// <para>
/// <c>playback_progress</c> is deliberately untouched at any age. It is keyed by (user, asset) and
/// upserted — it is where "continue watching" lives, which makes it state rather than history.
/// </para>
/// </summary>
public sealed class PurgePlaybackSessionsCommandHandler(
    PlaybackDbContext dbContext,
    PlaybackOptions options,
    TranscodeWorkspace workspace,
    RetentionOptions platformOptions,
    ILogger<PurgePlaybackSessionsCommandHandler> logger)
    : ICommandHandler<PurgePlaybackSessionsCommand>
{
    /// <summary>
    /// Shortest silence after which a session that never ended is treated as abandoned. It is a floor
    /// under the configured window, so shortening the window cannot start deleting live sessions.
    /// </summary>
    private static readonly TimeSpan AbandonedGrace = TimeSpan.FromDays(1);

    public async Task<Result> HandleAsync(
        PurgePlaybackSessionsCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var finishedCutoff = now - options.SessionRetention;
        var abandonedCutoff = now - (options.SessionRetention > AbandonedGrace
            ? options.SessionRetention
            : AbandonedGrace);

        var reclaimed = 0;

        var removed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Sessions,
            session =>
                ((session.State == PlaybackState.Completed || session.State == PlaybackState.Failed)
                    && session.UpdatedAt < finishedCutoff)
                || (session.State != PlaybackState.Completed
                    && session.State != PlaybackState.Failed
                    && session.UpdatedAt < abandonedCutoff),
            session => session.Id,
            (ids, token) =>
            {
                foreach (var id in ids)
                {
                    token.ThrowIfCancellationRequested();
                    if (workspace.Remove(id))
                    {
                        reclaimed++;
                    }

                    // This is the only place an abandoned session is ever recognised. A viewer whose
                    // browser died never sent a stop and never crossed the watched threshold, so without
                    // this the transcode gauge keeps its +1 for the life of the process and the number
                    // an operator uses to judge the node ratchets upward until it means nothing. Safe
                    // for every id: the gauge only reacts to sessions this process is still counting.
                    PlaybackMetrics.RecordStopped(id);
                }

                return Task.CompletedTask;
            },
            platformOptions.BatchSize,
            cancellationToken);

        logger.LogInformation(
            "Retention purge (playback): removed {Removed} sessions with their transcode jobs and reclaimed "
            + "{Reclaimed} transcode directories; resume progress is untouched.",
            removed,
            reclaimed);

        return Result.Success();
    }
}
