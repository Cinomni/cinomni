using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;
using Cinomni.Subtitles.Persistence;
using Cinomni.Subtitles.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// Drives the subtitle search for an asset. For each wanted language the asset is missing, it opens a
/// search, queries the providers (out-of-process), scores the candidates, downloads the best one over
/// the threshold, writes it next to the video, and registers it — persisting the search and its
/// <c>SubtitleAvailable</c> event in one unit of work. Idempotent per (asset, language,
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
    ILiveOptions<SubtitlePreference>? preference = null) : ISubtitleSearch
{
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
        var exists = await dbContext.Searches.AnyAsync(
            s => s.AssetId == assetId && s.Language == language && s.Forced == wanted.Forced && s.HearingImpaired == wanted.HearingImpaired,
            cancellationToken);
        if (exists)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
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
            scored.Select(s => (s.Provider.Name, s.Candidate.Release, s.Candidate.Score, s.Candidate.HearingImpaired, s.Candidate.DownloadRef)),
            now);
        var added = search.Candidates.Skip(before).ToList();

        var events = new List<IDomainEvent> { new SubtitleSearchRequested(search.Id, search.AssetId, language) };
        var best = scored
            .Where(s => s.Candidate.Score >= options.MinScore)
            .Where(s => s.Candidate.HearingImpaired == wanted.HearingImpaired && s.Candidate.Forced == wanted.Forced)
            .MaxBy(s => s.Candidate.Score);
        if (best is null)
        {
            search.MarkNotFound(now);
            events.Add(new SubtitleSearchFailed(search.Id, search.AssetId, language, search.Attempts));
            await PersistAsync(search, isNew, added, subtitleAsset: null, events, cancellationToken);
            return;
        }

        search.BeginDownload(now);
        try
        {
            var download = await best.Provider.DownloadAsync(best.Candidate.DownloadRef, cancellationToken);
            var path = await fileStore.WriteAsync(
                videoPath, language, wanted.Forced, wanted.HearingImpaired, download.Format, download.Content, cancellationToken);

            var subtitleAsset = SubtitleAsset.Create(
                search.AssetId, search.Id, language, wanted.Forced, wanted.HearingImpaired,
                download.Format, path, best.Provider.Name, best.Candidate.Score, now);
            search.MarkAvailable(subtitleAsset.Id, now);
            events.Add(new SubtitleAvailable(subtitleAsset.Id, search.AssetId, language, wanted.Forced, wanted.HearingImpaired, path));
            await PersistAsync(search, isNew, added, subtitleAsset, events, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // The file did not land. Record NotFound so the same backoff that reopens an empty search
            // reopens this one. Leaving it Downloading, or recording nothing, spends the retry key.
            logger.LogWarning(ex, "Subtitle download/write failed for asset {AssetId} ({Language}).", search.AssetId, language);
            search.MarkNotFound(now);
            events.Add(new SubtitleSearchFailed(search.Id, search.AssetId, language, search.Attempts));
            await PersistAsync(search, isNew, added, subtitleAsset: null, events, cancellationToken);
        }
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
            wanted.Forced);

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
        foreach (var stream in version.Streams.Where(s => s.Type == MediaStreamType.Subtitle && s.Language is not null))
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

    private async Task PersistAsync(
        SubtitleSearch search,
        bool isNew,
        IReadOnlyList<SubtitleCandidate> addedCandidates,
        SubtitleAsset? subtitleAsset,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken) =>
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

            await dbContext.SaveChangesAsync(token);
            foreach (var domainEvent in events)
            {
                await eventBus.PublishAsync(domainEvent, token);
            }
        }, cancellationToken);
}
