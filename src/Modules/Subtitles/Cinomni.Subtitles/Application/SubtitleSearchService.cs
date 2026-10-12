using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Persistence;
using Cinomni.Subtitles.Providers;
using Cinomni.Subtitles.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// Drives the subtitle search for an asset. For each wanted language the asset is missing, it opens a
/// search, queries the providers (out-of-process), scores the candidates, downloads the best one over
/// the threshold, writes it next to the video, and registers it — persisting the search and its
/// <c>SubtitleAvailable</c> event in one unit of work (with the pending synchronization row, when the
/// subtitle is to be synchronized). Idempotent per (asset, language,
/// forced, hi): a redelivered trigger reuses the existing search.
/// </summary>
public sealed class SubtitleSearchService(
    SubtitlesDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ILibraryQuery library,
    IEnumerable<ISubtitleProvider> providers,
    ISubtitleFileStore fileStore,
    EpisodeContextResolver episodeContext,
    SubtitleProviderThrottle throttle,
    SubtitleOptions options,
    ILogger<SubtitleSearchService> logger,
    ILiveOptions<SubtitlePreference>? preference = null,
    SubtitleSyncOptions? syncOptions = null) : ISubtitleSearch
{
    /// <summary>
    /// How many candidates one search may download before giving up until its next attempt. Each one
    /// spends a provider's daily download quota, so the bound is small.
    /// </summary>
    public const int MaxDownloadsPerSearch = 3;

    private sealed record Scored(ISubtitleProvider Provider, ProviderCandidate Candidate);

    public async Task SearchForAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = await library.GetAsync(new MediaAssetId(assetId), cancellationToken);
        if (asset is null || asset.Versions.Count == 0)
        {
            return;
        }

        var version = asset.Versions.FirstOrDefault(v => v.Id == asset.Asset.PrimaryVersionId) ?? asset.Versions[0];
        var release = Path.GetFileNameWithoutExtension(version.FullPath);
        var present = await PresentAsync(assetId, version, cancellationToken);

        // The asset carries the catalog units it serves (wave 3), so the episode is resolved once per
        // asset rather than once per language — and never by reaching back into Library per unit.
        var episode = await episodeContext.ResolveAsync(asset, cancellationToken);

        var wanted = Preference();
        foreach (var language in wanted.WantedLanguages)
        {
            if (SubtitleCoverage.IsSatisfied(language, wanted.Forced, wanted.HearingImpaired, present))
            {
                await RecordAlreadyPresentAsync(assetId, language, wanted, cancellationToken);
                continue;
            }

            await SearchLanguageAsync(assetId, version.FullPath, release, language, episode, wanted, cancellationToken);
        }
    }

    /// <summary>
    /// Writes down, once, that this language needs no search. Without a row the catch-up read it as
    /// missing on every pass. An existing row is left alone: it is the record of a search that ran.
    /// </summary>
    private async Task RecordAlreadyPresentAsync(
        Guid assetId, string language, SubtitlePreference wanted, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Searches.FirstOrDefaultAsync(
            s => s.AssetId == assetId && s.Language == language && s.Forced == wanted.Forced && s.HearingImpaired == wanted.HearingImpaired,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            // A search left NotFound for a subtitle the file turns out to carry is closed as covered, so the
            // catch-up stops returning to it. Any other row is the record of a search that ran: left alone.
            if (existing.State == SubtitleSearchState.NotFound)
            {
                existing.MarkAlreadyPresent(now);
                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another pass moved it first; whatever it recorded stands.
                    dbContext.ChangeTracker.Clear();
                }
            }

            return;
        }

        var search = SubtitleSearch.Create(assetId, language, wanted.Forced, wanted.HearingImpaired, now);
        search.MarkAlreadyPresent(now);
        dbContext.Searches.Add(search);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent search for the same key wrote its row first; that row answers the question.
            dbContext.Entry(search).State = EntityState.Detached;
        }
    }

    private async Task SearchLanguageAsync(
        Guid assetId,
        string videoPath,
        string release,
        string language,
        EpisodeContext episode,
        SubtitlePreference wanted,
        CancellationToken cancellationToken)
    {
        // Idempotent per (asset, language, forced, hi). A NotFound or abandoned download is reused, and
        // reopened only once its backoff has elapsed — never forked into a second search.
        var existing = await dbContext.Searches
            .Include(s => s.Candidates)
            .FirstOrDefaultAsync(
                s => s.AssetId == assetId && s.Language == language && s.Forced == wanted.Forced && s.HearingImpaired == wanted.HearingImpaired,
                cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            if ((existing.State is SubtitleSearchState.NotFound or SubtitleSearchState.Downloading)
                && SubtitleRetry.IsDue(existing.Attempts, existing.UpdatedAt, now))
            {
                await ContinueAsync(existing, videoPath, release, language, episode, wanted, isNew: false, now, cancellationToken);
            }

            return;
        }

        var search = SubtitleSearch.Create(assetId, language, wanted.Forced, wanted.HearingImpaired, now);
        await ContinueAsync(search, videoPath, release, language, episode, wanted, isNew: true, now, cancellationToken);
    }

    private async Task ContinueAsync(
        SubtitleSearch search,
        string videoPath,
        string release,
        string language,
        EpisodeContext episode,
        SubtitlePreference wanted,
        bool isNew,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var before = search.Candidates.Count;
        search.BeginSearch(now);

        var scored = await QueryProvidersAsync(release, language, episode, wanted, cancellationToken);
        search.RecordCandidates(
            scored.Select(s => (s.Provider.Name, s.Candidate.Release, s.Candidate.Score, s.Candidate.HearingImpaired, s.Candidate.DownloadRef, s.Candidate.Reason)),
            now);
        var added = search.Candidates.Skip(before).ToList();

        var events = new List<IDomainEvent> { new SubtitleSearchRequested(search.Id, search.AssetId, language) };
        // Best first, and more than one: a file can turn out not to be what it was labelled, or fail to
        // download, and the next good candidate is a better outcome than waiting hours to search again.
        var ranked = scored
            .Where(s => s.Candidate.Score >= options.MinScore)
            .Where(s => s.Candidate.HearingImpaired == wanted.HearingImpaired && s.Candidate.Forced == wanted.Forced)
            .OrderByDescending(s => s.Candidate.Score)
            .Take(MaxDownloadsPerSearch)
            .ToList();
        if (ranked.Count == 0)
        {
            search.MarkNotFound(now);
            events.Add(new SubtitleSearchFailed(search.Id, search.AssetId, language, search.Attempts));
            await PersistAsync(search, isNew, added, subtitleAsset: null, events, cancellationToken);
            return;
        }

        search.BeginDownload(now);
        foreach (var choice in ranked)
        {
            try
            {
                var download = await choice.Provider.DownloadAsync(choice.Candidate.DownloadRef, cancellationToken);
                if (!SubtitleLanguageCheck.IsPlausible(language, SubtitleTextDecoder.Decode(download.Content)))
                {
                    logger.LogWarning(
                        "Subtitle '{Release}' from {Provider} for asset {AssetId} is not written in the alphabet of {Language}; trying the next candidate.",
                        choice.Candidate.Release, choice.Provider.Name, search.AssetId, language);
                    continue;
                }

                var path = await fileStore.WriteAsync(
                    videoPath, language, wanted.Forced, wanted.HearingImpaired, download.Format, download.Content, cancellationToken);

                var subtitleAsset = SubtitleAsset.Create(
                    search.AssetId, search.Id, language, wanted.Forced, wanted.HearingImpaired,
                    download.Format, path, choice.Provider.Name, choice.Candidate.Score, now);
                var landing = Land(search, subtitleAsset, now);
                events.Add(new SubtitleAvailable(subtitleAsset.Id, search.AssetId, language, wanted.Forced, wanted.HearingImpaired, path));
                await PersistAsync(search, isNew, added, subtitleAsset, events, cancellationToken, landing);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Subtitle download/write failed for asset {AssetId} ({Language}).", search.AssetId, language);
            }
        }

        // Nothing landed. Record NotFound so the same backoff that reopens an empty search reopens this
        // one. Leaving it Downloading, or recording nothing, spends the retry key.
        search.MarkNotFound(now);
        events.Add(new SubtitleSearchFailed(search.Id, search.AssetId, language, search.Attempts));
        await PersistAsync(search, isNew, added, subtitleAsset: null, events, cancellationToken);
    }

    private async Task<IReadOnlyList<Scored>> QueryProvidersAsync(
        string release,
        string language,
        EpisodeContext episode,
        SubtitlePreference wanted,
        CancellationToken cancellationToken)
    {
        var query = new SubtitleProviderQuery(
            release,
            language,
            wanted.HearingImpaired,
            episode.SeriesTitle,
            episode.SeasonNumber,
            episode.EpisodeNumber,
            episode.ParentImdbId,
            wanted.Forced,
            episode.MovieTitle,
            episode.MovieYear,
            episode.MovieImdbId);

        var results = new List<Scored>();

        // A provider with no credentials cannot answer, so it must not cost a throttle slot either: the
        // default install registers the adapter without an API key, and paying the stagger for a call
        // that never leaves the process is pure latency on a shared worker.
        foreach (var provider in providers.Where(p => p.IsConfigured))
        {
            try
            {
                // Throttled: a season pack registers N assets at once and each fires its own search.
                var candidates = await throttle.RunAsync(token => provider.SearchAsync(query, token), cancellationToken);
                results.AddRange(candidates.Select(c => new Scored(provider, c)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Subtitle provider {Provider} search failed.", provider.Name);
            }
        }

        return results;
    }

    private async Task<List<(string Language, bool Forced, bool HearingImpaired)>> PresentAsync(
        Guid assetId,
        MediaVersionSummary version,
        CancellationToken cancellationToken)
    {
        var present = new List<(string Language, bool Forced, bool HearingImpaired)>();

        // Only a text track covers a language. A picture one (PGS, VobSub) is shown only by burning it into
        // the video, a full re-encode that a 4K file cannot get in real time; a text sidecar plays directly.
        foreach (var stream in version.Streams.Where(s => SubtitleStreams.IsText(s) && s.Language is not null))
        {
            // An embedded track carries forced, not hearing-impaired. It must not satisfy an HI request.
            present.Add((stream.Language!, stream.IsForced, false));
        }

        var downloaded = await dbContext.Assets
            .Where(a => a.AssetId == assetId)
            .Select(a => new { a.Language, a.Forced, a.HearingImpaired })
            .ToListAsync(cancellationToken);
        present.AddRange(downloaded.Select(a => (a.Language, a.Forced, a.HearingImpaired)));
        return present;
    }

    private SubtitlePreference Preference()
    {
        if (preference is not null)
        {
            return preference.Current;
        }

        return new SubtitlePreference
        {
            WantedLanguages = options.WantedLanguages,
            HearingImpaired = options.HearingImpaired,
            Forced = options.Forced,
        };
    }

    /// <summary>
    /// Lands a downloaded subtitle. With synchronization on, a SubRip or WebVTT file goes to
    /// <see cref="SubtitleSearchState.Syncing"/> with a pending row for the sync worker; anything it does
    /// not cover is recorded as unsupported and lands available at once, so the reason is on record
    /// rather than implied by a missing row. With synchronization off (or not wired, as in a test host
    /// without the adapters) it lands available as it always did. Either way the subtitle is playable
    /// now: its row points at the file as downloaded.
    /// </summary>
    private Landing Land(SubtitleSearch search, SubtitleAsset subtitleAsset, DateTimeOffset now)
    {
        if (syncOptions is not { Enabled: true })
        {
            search.MarkAvailable(subtitleAsset.Id, now);
            return new Landing(null, null);
        }

        if (SubtitleSyncAnalyzer.Unsupported(subtitleAsset.Format, subtitleAsset.Forced, syncOptions) is { } reason)
        {
            search.MarkAvailable(subtitleAsset.Id, now);
            return new Landing(null, SubtitleSyncDecision.Unmeasured(subtitleAsset.Id, SubtitleSyncOutcome.Unsupported, reason, now));
        }

        search.BeginSync(subtitleAsset.Id, now);
        return new Landing(SubtitleSyncWork.Create(subtitleAsset.Id, search.Id, now), null);
    }

    private sealed record Landing(SubtitleSyncWork? Work, SubtitleSyncDecision? Decision);

    private async Task PersistAsync(
        SubtitleSearch search,
        bool isNew,
        IReadOnlyList<SubtitleCandidate> addedCandidates,
        SubtitleAsset? subtitleAsset,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken,
        Landing? landing = null) =>
        await unitOfWork.ExecuteAsync(async token =>
        {
            if (isNew)
            {
                dbContext.Searches.Add(search); // new root → cascade inserts the candidates
            }
            else
            {
                dbContext.Candidates.AddRange(addedCandidates);
            }

            if (subtitleAsset is not null)
            {
                dbContext.Assets.Add(subtitleAsset);
            }

            // The pending sync row commits with the subtitle and its Syncing state, or not at all: a
            // search can never be left synchronizing with nothing queued to finish it.
            if (landing?.Work is { } work)
            {
                dbContext.SyncWork.Add(work);
            }

            if (landing?.Decision is { } decision)
            {
                dbContext.SyncDecisions.Add(decision);
            }

            await dbContext.SaveChangesAsync(token);
            foreach (var domainEvent in events)
            {
                await eventBus.PublishAsync(domainEvent, token);
            }
        }, cancellationToken);
}
