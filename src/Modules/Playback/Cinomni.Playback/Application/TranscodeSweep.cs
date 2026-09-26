using Cinomni.Playback.Contracts;
using Cinomni.Playback.Encoding;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Application;

/// <summary>
/// One pass over the transcodes this node is responsible for, run on a short cadence by
/// <see cref="TranscodeReaper"/>. Three jobs, in this order:
/// <list type="number">
/// <item>Record how every FFmpeg that ended on its own ended. Having written the whole file changes
/// only the job; failing part way also fails the session and ends the stream, because its playlist
/// will never be finished and a player left polling it would buffer for ever.</item>
/// <item>Close every stream nothing has asked for within the idle timeout — the viewer closed the tab
/// or lost the network and nothing will ever say so — and every stream that has reached the longest a
/// transcode may run, however busy its client keeps it.</item>
/// <item>Reconcile what the registry does not hold with what the database and the disk say: an open
/// session with no running transcode, and an output directory no running transcode owns. After a
/// restart that is everything the previous run left — a stream it had already finished converting is
/// adopted, so a viewer mid-film keeps watching; anything still being converted died with that run, so
/// its session is failed, its partial output removed, and — should it have outlived the host that
/// started it — its FFmpeg stopped. Within a run it is the close whose save never landed.</item>
/// </list>
/// A row is always saved before the directory it names is removed: a pass interrupted in between
/// leaves the directory for the next pass to find. Each session is handled on its own, and a failure in
/// one never stops the rest.
/// </summary>
public sealed class TranscodeSweep(
    PlaybackDbContext dbContext,
    SessionUpdates updates,
    ActiveTranscodes transcodes,
    TranscodeWorkspace workspace,
    OrphanedTranscodeTerminator orphans,
    ILogger<TranscodeSweep> logger)
{
    internal const string RestartReason = "The server restarted while this stream was being converted.";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await RecordExitsAsync(cancellationToken);
        await CloseIdleAsync(cancellationToken);
        await CloseExpiredAsync(cancellationToken);
        await ReconcileAsync(cancellationToken);
    }

    private async Task RecordExitsAsync(CancellationToken cancellationToken)
    {
        foreach (var exit in transcodes.PendingExits())
        {
            await EachAsync(exit.SessionId, "record how the transcode ended", async () =>
            {
                var failed = false;
                var found = await updates.ApplyAsync(exit.SessionId, session =>
                {
                    if (session.RecordTranscodeExit(exit.ExitCode, exit.Diagnostics, DateTimeOffset.UtcNow) is not { } jobs)
                    {
                        return null;
                    }

                    failed = jobs.Count > 0;
                    // The exit code only: what FFmpeg said can quote the media path, and it is on the job row.
                    return [.. jobs.Select(job => new TranscodeFailed(job.Id, $"FFmpeg exited with code {exit.ExitCode}."))];
                }, cancellationToken);

                if (!found)
                {
                    return; // the session is still being opened; its row commits in a moment
                }

                if (failed)
                {
                    await transcodes.EndAsync(exit.SessionId);
                    logger.LogWarning(
                        "The transcode of session {SessionId} failed part way through (exit code {Code}).",
                        exit.SessionId, exit.ExitCode);
                }
                else
                {
                    transcodes.ExitRecorded(exit.SessionId);
                }
            });
        }
    }

    private async Task CloseIdleAsync(CancellationToken cancellationToken)
    {
        foreach (var sessionId in transcodes.Idle())
        {
            await EachAsync(sessionId, "close the idle session", async () =>
            {
                if (!await transcodes.EndIfIdleAsync(sessionId))
                {
                    return; // somebody asked for it after all
                }

                await updates.ApplyAsync(
                    sessionId, session => SessionCloser.Close(session, PlaybackEndReason.Idle), cancellationToken);
                logger.LogInformation(
                    "Closed the transcode of session {SessionId}: nothing asked for it in the idle window.", sessionId);
            });
        }
    }

    private async Task CloseExpiredAsync(CancellationToken cancellationToken)
    {
        foreach (var sessionId in transcodes.Expired())
        {
            await EachAsync(sessionId, "close the expired session", async () =>
            {
                // Past its lifetime is past it for good, so there is nothing to re-check under the lock.
                await transcodes.EndAsync(sessionId);
                await updates.ApplyAsync(
                    sessionId, session => SessionCloser.Close(session, PlaybackEndReason.Expired), cancellationToken);
                logger.LogInformation(
                    "Closed the transcode of session {SessionId}: it reached the longest a transcode may run.", sessionId);
            });
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var open = await dbContext.Sessions
            .AsNoTracking()
            .Where(s => s.State != PlaybackState.Completed
                && s.State != PlaybackState.Failed
                && s.Method != PlaybackMethod.DirectPlay)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        // A job still marked running names a process that may still be alive even when its session has
        // ended (the watched threshold) and its directory is gone.
        var running = await dbContext.TranscodeJobs
            .AsNoTracking()
            .Where(job => job.State == TranscodeState.Running)
            .Select(job => job.SessionId)
            .ToListAsync(cancellationToken);

        var untracked = open
            .Concat(running)
            .Concat(workspace.SessionDirectories())
            .Distinct()
            .Where(id => !transcodes.IsTracked(id))
            .ToList();

        foreach (var sessionId in untracked)
        {
            await EachAsync(sessionId, "reconcile the transcode", () => ReconcileAsync(sessionId, cancellationToken));
        }
    }

    private async Task ReconcileAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .AsNoTracking()
            .Include(s => s.TranscodeJobs)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            // No row names it: a session id is only a GUID, so the directory is removed only if what it
            // holds is unmistakably a transcode's output.
            if (workspace.HoldsOnlyTranscodeOutput(sessionId))
            {
                workspace.Remove(sessionId);
            }
            else if (workspace.HasOutput(sessionId))
            {
                logger.LogWarning(
                    "Left the directory of unknown session {SessionId} under the transcode root alone: it holds "
                    + "something other than transcode output.",
                    sessionId);
            }

            return;
        }

        var fromPreviousRun = session.StartedAt < transcodes.StartedAt;
        if (fromPreviousRun && !session.HasEnded
            && session.TranscodeJobs.Any(job => job.State == TranscodeState.Completed)
            && workspace.HasOutput(sessionId))
        {
            transcodes.Adopt(session.Id, session.UserId, session.StartedAt);
            logger.LogInformation(
                "Adopted the finished transcode of session {SessionId} from before the restart.", sessionId);
            return;
        }

        if (fromPreviousRun && !await StopOrphansAsync(session))
        {
            // Its FFmpeg is still running and could not be stopped: the row stays as it is, naming the
            // process, and the next pass tries again rather than reclaiming a directory the encoder
            // would only write into again.
            return;
        }

        var interrupted = false;
        await updates.ApplyAsync(sessionId, row =>
        {
            var now = DateTimeOffset.UtcNow;
            if (fromPreviousRun)
            {
                // An open session cannot go on without the process that was converting it.
                var failedJobs = row.Interrupt(RestartReason, now);
                interrupted = failedJobs is not null;
                var released = row.ReleaseTranscode(now);
                return failedJobs is { } jobs
                    ? [.. jobs.Select(job => new TranscodeFailed(job.Id, RestartReason))]
                    : released ? [] : null;
            }

            // This run's: a close whose save never landed. Finish it; why it was closed is not known.
            return SessionCloser.Close(row, PlaybackEndReason.Recovered);
        }, cancellationToken);

        await transcodes.EndAsync(sessionId);
        if (interrupted)
        {
            logger.LogInformation("Failed session {SessionId}: its transcode did not survive the restart.", sessionId);
        }
    }

    /// <summary>
    /// Stops any FFmpeg a previous run started for this session and never stopped — it outlives its
    /// host when that host crashes outside a container. Only the recorded process, recognised by every
    /// fact <see cref="OrphanedTranscodeTerminator"/> checks.
    /// </summary>
    /// <returns><c>false</c> when one is still running and could not be stopped.</returns>
    private async Task<bool> StopOrphansAsync(PlaybackSession session)
    {
        foreach (var job in session.TranscodeJobs)
        {
            if (job.State != TranscodeState.Running
                || job.ProcessId is not { } processId
                || job.ProcessStartedAt is not { } startedAt
                || job.OutputPath is not { } outputPath)
            {
                continue;
            }

            switch (await orphans.TryTerminateAsync(processId, startedAt, outputPath))
            {
                case OrphanOutcome.Stopped:
                    logger.LogWarning(
                        "Stopped the FFmpeg process {ProcessId} that a previous run left converting session {SessionId}.",
                        processId, session.Id);
                    break;
                case OrphanOutcome.Survived:
                    return false;
            }
        }

        return true;
    }

    private async Task EachAsync(Guid sessionId, string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogWarning(failure, "Could not {What} for session {SessionId}; the next pass tries again.", what, sessionId);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }
}
