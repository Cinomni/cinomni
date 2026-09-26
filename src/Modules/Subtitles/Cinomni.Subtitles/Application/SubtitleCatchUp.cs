using Cinomni.Library.Contracts;
using Cinomni.Subtitles.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Cinomni.Subtitles.Messaging;
using Cinomni.Subtitles.Persistence;
using Cinomni.Subtitles.Providers;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// Enqueues a search for library assets that do not yet have a search for the current preference.
/// Bounded, so one pass cannot turn a large library into a provider burst. The next pass continues
/// where this one stopped, because an asset that already has a row is skipped.
/// </summary>
public sealed class SubtitleCatchUp(
    SubtitlesDbContext dbContext,
    ILibraryQuery library,
    ICommandQueue commandQueue,
    SubtitleProviderThrottle throttle,
    IEnumerable<ISubtitleProvider> providers,
    ILiveOptions<SubtitlePreference> preference)
{
    public const int MaxPerRun = 20;

    /// <summary>
    /// Enqueues one pass may try at all, accepted or not. A refused one still reserves provider time in
    /// the shared throttle, so without a bound a pass over assets already queued could push every later
    /// search, a fresh import's included, hours out.
    /// </summary>
    public const int MaxAttemptsPerRun = MaxPerRun * 5;

    public async Task<int> EnqueueAsync(CancellationToken cancellationToken = default)
    {
        var wanted = preference.Current;
        if (wanted.WantedLanguages.Count == 0 || !providers.Any(p => p.IsConfigured))
        {
            return 0;
        }

        var assets = await library.ListAsync(cancellationToken);
        var ids = assets.Select(a => a.Id.Value).ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var known = await dbContext.Searches
            .AsNoTracking()
            .Where(s => ids.Contains(s.AssetId) && s.Forced == wanted.Forced && s.HearingImpaired == wanted.HearingImpaired)
            .Select(s => new { s.AssetId, s.Language, s.State, s.Attempts, s.UpdatedAt })
            .ToListAsync(cancellationToken);

        var stamp = SubtitleCoverage.Stamp(wanted);
        var calls = providers.Count(p => p.IsConfigured) * wanted.WantedLanguages.Count;
        var queued = 0;
        var attempted = 0;

        foreach (var asset in assets)
        {
            if (queued >= MaxPerRun || attempted >= MaxAttemptsPerRun)
            {
                break;
            }

            var dueAttempt = 0;
            var covered = wanted.WantedLanguages.All(language =>
            {
                var row = known.FirstOrDefault(item => item.AssetId == asset.Id.Value
                    && string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    return false;
                }

                if ((row.State is SubtitleSearchState.NotFound or SubtitleSearchState.Downloading)
                    && SubtitleRetry.IsDue(row.Attempts, row.UpdatedAt, now))
                {
                    dueAttempt = row.Attempts;
                    return false;
                }

                return true;
            });
            if (covered)
            {
                continue;
            }

            var command = new SearchSubtitlesCommand(asset.Id.Value);
            var key = dueAttempt == 0
                ? $"search-subtitles:{asset.Id.Value}:{stamp}"
                : $"search-subtitles:{asset.Id.Value}:{stamp}:retry:{dueAttempt}";
            attempted++;
            var runAfter = throttle.Reserve(calls);
            var accepted = runAfter is null
                ? await commandQueue.EnqueueAsync(command, key, cancellationToken)
                : await commandQueue.EnqueueAtAsync(command, key, runAfter.Value, cancellationToken);

            // Only a search that was actually queued uses up the pass. One already queued under this
            // key is refused, and counting it let the same few assets fill every pass while the
            // backlog behind them never got a turn.
            if (accepted)
            {
                queued++;
            }
        }

        return queued;
    }
}
