using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Cinomni.Playback.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Playback.Application;

/// <summary>
/// Applies one change to one session and saves it with its events, re-reading and re-applying the
/// change when a concurrent writer got there first.
/// <para>
/// The session row is written by the viewer (progress reports, stop) and by the sweep (recorded exits,
/// idle closes, restart recovery), and its <c>Version</c> is a concurrency token. The player sends its
/// last progress report and its stop at the same moment when it unmounts, so a conflict is the normal
/// case, not the exotic one — and a close that lost it used to leave the row open for good, with its
/// process already stopped and nothing left to retry it. Every change applied here is idempotent, so
/// applying it again to what the other writer saved is always safe.
/// </para>
/// </summary>
public sealed class SessionUpdates(PlaybackDbContext dbContext, IUnitOfWork unitOfWork, IEventBus eventBus)
{
    private const int MaxAttempts = 3;

    /// <param name="change">
    /// Applied to the session freshly loaded with its jobs. Returns the events to publish with the save,
    /// or <c>null</c> when it changed nothing and there is nothing to save.
    /// </param>
    /// <returns><c>false</c> when the session does not exist (yet, or any more).</returns>
    public async Task<bool> ApplyAsync(
        Guid sessionId,
        Func<PlaybackSession, IReadOnlyList<IDomainEvent>?> change,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            dbContext.ChangeTracker.Clear();
            var session = await dbContext.Sessions
                .Include(s => s.TranscodeJobs)
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is null)
            {
                return false;
            }

            if (change(session) is not { } events)
            {
                return true;
            }

            try
            {
                await unitOfWork.ExecuteAsync(async token =>
                {
                    await dbContext.SaveChangesAsync(token);
                    foreach (var domainEvent in events)
                    {
                        await eventBus.PublishAsync(domainEvent, token);
                    }
                }, cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Someone else saved this session in the meantime: re-read it and apply the change to that.
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }
}
