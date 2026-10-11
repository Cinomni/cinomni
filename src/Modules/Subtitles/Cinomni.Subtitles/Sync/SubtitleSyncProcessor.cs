using Cinomni.Operations.Transactions;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Sync;

/// <summary>
/// Works through the pending synchronization queue one subtitle at a time: claims the oldest row,
/// analyzes outside any transaction, writes the synchronized copy if the verdict is to apply it, and
/// then, in one short unit of work, appends the decision, points the subtitle at the file to play,
/// completes the search and removes the row.
/// <para>
/// Idempotent and resumable. The row is removed only in the unit of work that records the decision,
/// and removing it is the first thing that unit does: a second completion of the same subtitle (a
/// duplicate claim, the sweep racing a slow pass) deletes nothing and stops there. A pass interrupted
/// anywhere before that commit leaves the row, which is claimed again after a restart and rewrites the
/// same synchronized copy, because the output depends on nothing but the original and the verdict.
/// </para>
/// <para>
/// A failure never holds the subtitle back: it is playable from the moment it lands, and whatever the
/// pass runs into, it ends with the search <see cref="SubtitleSearchState.Available"/> and a recorded
/// reason.
/// </para>
/// </summary>
public sealed class SubtitleSyncProcessor(
    SubtitlesDbContext dbContext,
    IUnitOfWork unitOfWork,
    SubtitleSyncAnalyzer analyzer,
    ISubtitleFileStore fileStore,
    SubtitleSyncOptions options,
    ILogger<SubtitleSyncProcessor> logger)
{
    /// <summary>How many rows a claim tries before giving up for this tick, when other workers keep winning.</summary>
    private const int MaxClaimTries = 5;

    /// <summary>
    /// Releases every claim. Called once when the worker starts: it is the only owner of claims in this
    /// process, so any claim still standing was left by a run that stopped mid-pass.
    /// </summary>
    public async Task ReleaseClaimsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.SyncWork
            .Where(work => work.ClaimedAt != null)
            .ExecuteUpdateAsync(set => set.SetProperty(work => work.ClaimedAt, (DateTimeOffset?)null), cancellationToken);

    /// <summary>Processes the oldest pending subtitle. False when there was nothing to do.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var work = await ClaimNextAsync(cancellationToken);
        if (work is null)
        {
            return false;
        }

        await ProcessAsync(work, cancellationToken);
        return true;
    }

    /// <summary>
    /// Closes searches that have been synchronizing for longer than <see cref="SubtitleSyncOptions.StuckAfter"/>
    /// with nothing making progress on them: no pending row, or a row claimed that long ago. A row still
    /// waiting its turn is not stuck, only queued. Returns how many were closed.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now - options.StuckAfter;
        var stuck = await dbContext.Searches
            .AsNoTracking()
            .Where(search => search.State == SubtitleSearchState.Syncing && search.UpdatedAt < cutoff)
            .Select(search => new { search.Id, search.SubtitleAssetId })
            .ToListAsync(cancellationToken);

        var closed = 0;
        foreach (var search in stuck)
        {
            var work = await dbContext.SyncWork.AsNoTracking()
                .FirstOrDefaultAsync(row => row.SearchId == search.Id, cancellationToken);
            if (work is not null && (work.ClaimedAt is null || work.ClaimedAt > cutoff))
            {
                continue;
            }

            var hours = options.StuckAfter.TotalHours;
            var why = hours >= 1
                ? $"synchronization did not finish within {hours:0.#} h"
                : $"synchronization did not finish within {options.StuckAfter.TotalMinutes:0} min";
            var verdict = SyncVerdict.Unmeasured(SubtitleSyncOutcome.Failed, why);
            if (await CompleteAsync(search.Id, search.SubtitleAssetId ?? work?.SubtitleAssetId, verdict, syncedPath: null,
                    analyzedOriginal: null, hasWork: work is not null, now, cancellationToken))
            {
                closed++;
            }
        }

        return closed;
    }

    private async Task<SubtitleSyncWork?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        for (var tries = 0; tries < MaxClaimTries; tries++)
        {
            var next = await dbContext.SyncWork
                .AsNoTracking()
                .Where(work => work.ClaimedAt == null)
                .OrderBy(work => work.RequestedAt)
                .Select(work => work.SubtitleAssetId)
                .FirstOrDefaultAsync(cancellationToken);
            if (next == Guid.Empty)
            {
                return null;
            }

            // A conditional update is the claim: of two workers reading the same row, one updates it
            // and the other updates nothing and tries the next.
            var now = DateTimeOffset.UtcNow;
            var claimed = await dbContext.SyncWork
                .Where(work => work.SubtitleAssetId == next && work.ClaimedAt == null)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(work => work.ClaimedAt, now).SetProperty(work => work.Attempts, work => work.Attempts + 1),
                    cancellationToken);
            if (claimed == 1)
            {
                return await dbContext.SyncWork.AsNoTracking()
                    .FirstOrDefaultAsync(work => work.SubtitleAssetId == next, cancellationToken);
            }
        }

        return null;
    }

    private async Task ProcessAsync(SubtitleSyncWork work, CancellationToken cancellationToken)
    {
        var subtitle = await dbContext.Assets.AsNoTracking()
            .FirstOrDefaultAsync(asset => asset.Id == work.SubtitleAssetId, cancellationToken);
        var search = await dbContext.Searches.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == work.SearchId, cancellationToken);
        if (subtitle is null || search is not { State: SubtitleSearchState.Syncing })
        {
            // Already decided, or nothing left to decide about: a stale row, removed without a decision.
            await dbContext.SyncWork.Where(row => row.SubtitleAssetId == work.SubtitleAssetId).ExecuteDeleteAsync(cancellationToken);
            return;
        }

        var (verdict, shifted) = await AnalyzeAsync(work, subtitle, cancellationToken);

        string? syncedPath = null;
        if (verdict.Outcome == SubtitleSyncOutcome.Applied && shifted is not null)
        {
            try
            {
                syncedPath = await fileStore.WriteSyncedAsync(subtitle.OriginalPath, shifted, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(ex, "The synchronized copy of subtitle {SubtitleId} could not be written.", subtitle.Id);
                verdict = verdict with
                {
                    Outcome = SubtitleSyncOutcome.Failed,
                    Reason = "the synchronized copy could not be written",
                };
            }
        }

        await CompleteAsync(search.Id, subtitle.Id, verdict, syncedPath, subtitle.OriginalPath, hasWork: true,
            DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task<SyncAnalysis> AnalyzeAsync(SubtitleSyncWork work, SubtitleAsset subtitle, CancellationToken cancellationToken)
    {
        if (work.Attempts > options.MaxAttempts)
        {
            return new SyncAnalysis(
                SyncVerdict.Unmeasured(SubtitleSyncOutcome.Failed, $"synchronization was interrupted {work.Attempts - 1} times"),
                null);
        }

        try
        {
            return await analyzer.AnalyzeAsync(
                new SyncSubject(subtitle.AssetId, subtitle.Language, subtitle.Forced, subtitle.Format, subtitle.OriginalPath),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Shutting down is the one exception that is not a verdict: the row stays claimed and the
            // next start releases it and runs the pass again. Anything else is this subtitle's problem.
            logger.LogWarning(ex, "Synchronization of subtitle {SubtitleId} failed.", subtitle.Id);
            return new SyncAnalysis(SyncVerdict.Unmeasured(SubtitleSyncOutcome.Failed, "the analysis failed unexpectedly"), null);
        }
    }

    /// <summary>
    /// Records the verdict and completes the search in one unit of work. False when somebody else got
    /// there first (the pending row was already gone, or the search is no longer synchronizing), in which
    /// case nothing is written.
    /// </summary>
    private async Task<bool> CompleteAsync(
        Guid searchId,
        Guid? subtitleId,
        SyncVerdict verdict,
        string? syncedPath,
        string? analyzedOriginal,
        bool hasWork,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string? orphan = null;
        var completed = false;
        await unitOfWork.ExecuteAsync(async token =>
        {
            // First, and the arbiter: deleting the row takes its lock, so a second completion running
            // at the same time waits here and then finds nothing to delete.
            if (hasWork && subtitleId is { } id
                && await dbContext.SyncWork.Where(row => row.SubtitleAssetId == id).ExecuteDeleteAsync(token) == 0)
            {
                return;
            }

            var search = await dbContext.Searches.FirstOrDefaultAsync(row => row.Id == searchId, token);
            if (search is not { State: SubtitleSearchState.Syncing })
            {
                return;
            }

            var subtitle = subtitleId is { } known
                ? await dbContext.Assets.FirstOrDefaultAsync(asset => asset.Id == known, token)
                : null;
            if (subtitle is not null)
            {
                var playing = subtitle.Path;
                var final = Settle(subtitle, verdict, syncedPath, analyzedOriginal);
                dbContext.SyncDecisions.Add(SubtitleSyncDecision.Create(
                    subtitle.Id, final.Outcome, (int)Math.Clamp(final.OffsetMs, int.MinValue, int.MaxValue), final.Ratio,
                    final.Score, final.Margin, final.Reason, now));

                // The copy played until now, if no longer played, goes once this commits.
                if (subtitle.Path != playing && SyncedSidecar.IsSyncedName(Path.GetFileName(playing)))
                {
                    orphan = playing;
                }
            }

            search.CompleteSync(now);
            await dbContext.SaveChangesAsync(token);
            completed = true;
        }, cancellationToken);

        if (orphan is not null)
        {
            await DeleteQuietlyAsync(orphan, cancellationToken);
        }

        return completed;
    }

    /// <summary>
    /// Points the subtitle at the file its verdict says to play and returns the verdict as recorded.
    /// A verdict about the original (applied, in sync, low confidence, unsupported) decides the file;
    /// a failure decides nothing, so whatever was played before is kept, and the reason says which.
    /// </summary>
    private static SyncVerdict Settle(SubtitleAsset subtitle, SyncVerdict verdict, string? syncedPath, string? analyzedOriginal)
    {
        // The video, and the subtitle with it, moved while the pass ran: the copy was written next to
        // where the original used to be, and is not what should be played.
        if (analyzedOriginal is not null && !string.Equals(subtitle.OriginalPath, analyzedOriginal, StringComparison.Ordinal))
        {
            verdict = SyncVerdict.Unmeasured(SubtitleSyncOutcome.Failed, "the video moved while it was analyzed");
            syncedPath = null;
        }

        if (verdict.Outcome == SubtitleSyncOutcome.Failed)
        {
            var kept = subtitle.IsSynced ? "Kept the current synchronized copy" : "Kept the original";
            return verdict with { Reason = $"{kept} because {verdict.Reason}." };
        }

        if (verdict.Outcome == SubtitleSyncOutcome.Applied && syncedPath is not null)
        {
            subtitle.UseSynced(syncedPath);
        }
        else
        {
            subtitle.UseOriginal();
        }

        return verdict;
    }

    private async Task DeleteQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await fileStore.DeleteSyncedAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover copy is untidy, not wrong: nothing points at it any more.
            logger.LogWarning(ex, "A synchronized subtitle copy no longer used could not be removed.");
        }
    }
}
