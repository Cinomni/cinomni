using System.Collections.ObjectModel;
using System.Diagnostics;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Diagnostics;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Identifiers;
using Cinomni.Operations.Transactions;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Application;

/// <summary>
/// Federates the enabled indexers on a neutral criterion: fans out in parallel, deduplicates by
/// release identity (indexer priority breaks ties), persists the execution and its results, and
/// returns them. It never judges quality — that is Decision's job downstream.
/// </summary>
public sealed class ReleaseSearch(
    DiscoveryDbContext dbContext,
    IUnitOfWork unitOfWork,
    IIndexerClient indexerClient,
    IndexerCredentialProtector credentials,
    ILogger<ReleaseSearch> logger)
    : IReleaseSearch, IReleaseSearchResults
{
    public async Task<SearchOutcome> SearchAsync(
        SearchCriterion criterion,
        SearchOrigin origin = default,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var indexers = await dbContext.Indexers
            .AsNoTracking()
            .Where(i => i.Enabled)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Id) // stable tie-break so equal-priority dedup winners are deterministic
            .ToListAsync(cancellationToken);

        // Every definition the fan-out will need, read here and not there. The scoped DbContext (and
        // the single connection under it) belongs to one operation at a time, so a definition-backed
        // adapter that looked its own document up inside Task.WhenAll raced its siblings; the loser
        // threw and was swallowed into "that indexer returned nothing", invisibly.
        var definitions = await LoadDefinitionsAsync(indexers, cancellationToken);

        // Fan out over indexers in parallel. HTTP happens outside any transaction; a failing
        // indexer yields no results instead of failing the whole search (best-effort). Nothing in
        // here may touch the database — see LoadDefinitionsAsync above and IIndexerClient.
        var perIndexer = await Task.WhenAll(
            indexers.Select(indexer => SearchOneAsync(indexer, definitions, criterion, cancellationToken)));

        var deduplicated = Deduplicate(perIndexer);

        var execution = new SearchExecution
        {
            Id = Uuid7.New(),
            Term = criterion.Term,
            Year = criterion.Year,
            ContentKind = criterion.ContentKind,
            TargetId = origin.TargetId,
            WorkId = origin.WorkId,
            SeasonNumber = criterion.SeasonNumber,
            EpisodeNumber = criterion.EpisodeNumber,
            AbsoluteNumber = criterion.AbsoluteNumber,
            AirDate = criterion.AirDate,
            TvdbId = criterion.TvdbId,
            ImdbId = criterion.ImdbId,
            RequestedUnitIds = origin.UnitIds?.ToArray() ?? [],
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ResultCount = deduplicated.Count,
        };

        // Results are siblings, not children: both tables are partitioned by month, so each row
        // carries its own partition key (FoundAt = the execution's StartedAt, which co-locates the
        // two months) and there is no cross-partition foreign key to hang a navigation off.
        var results = new List<SearchResult>(deduplicated.Count);

        foreach (var (indexerId, candidate) in deduplicated)
        {
            results.Add(new SearchResult
            {
                Id = Uuid7.New(),
                ExecutionId = execution.Id,
                FoundAt = startedAt,
                // Already shaped to the columns by ToStorable, in SearchOneAsync.
                ReleaseGuid = candidate.Guid,
                Title = candidate.Title,
                DownloadUrl = candidate.DownloadUrl,
                Protocol = candidate.Protocol,
                SizeBytes = candidate.SizeBytes,
                Seeders = candidate.Seeders,
                Leechers = candidate.Leechers,
                PublishedAt = candidate.PublishedAt,
                IndexerName = candidate.IndexerName,
                IndexerId = indexerId,
                SeasonNumber = candidate.SeasonNumber,
                EpisodeNumber = candidate.EpisodeNumber,
                TvdbId = candidate.TvdbId,
                Category = candidate.Category,
            });
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.SearchExecutions.Add(execution);
            dbContext.SearchResults.AddRange(results);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);

        return new SearchOutcome(
            new SearchExecutionId(execution.Id), deduplicated.Select(x => x.Candidate).ToList());
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> GetResultsAsync(
        SearchExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        var results = await dbContext.SearchResults
            .AsNoTracking()
            .Where(r => r.ExecutionId == executionId.Value)
            .ToListAsync(cancellationToken);

        return results
            .Select(r => new ReleaseCandidate(
                r.ReleaseGuid, r.Title, r.DownloadUrl, r.Protocol, r.SizeBytes, r.Seeders, r.PublishedAt, r.IndexerName,
                r.SeasonNumber, r.EpisodeNumber, r.TvdbId, r.Category, r.Leechers))
            .ToList();
    }

    public async Task<SearchRequestContext?> GetRequestContextAsync(
        SearchExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        var execution = await dbContext.SearchExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId.Value, cancellationToken);

        return execution is null
            ? null
            : new SearchRequestContext(
                new SearchExecutionId(execution.Id),
                execution.Term,
                execution.Year,
                execution.ContentKind,
                execution.TargetId,
                execution.WorkId,
                execution.SeasonNumber,
                execution.EpisodeNumber,
                execution.AbsoluteNumber,
                execution.AirDate,
                execution.TvdbId,
                execution.ImdbId,
                execution.RequestedUnitIds);
    }

    /// <summary>
    /// Reads, in one query on the scoped context, the raw definition document of every enabled
    /// definition-backed indexer. Distinct by id, so several indexers sharing one definition cost one
    /// row. A missing id is simply absent from the result: the adapter reports that as the broken
    /// configuration it is, exactly as it did when it looked the row up itself.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> LoadDefinitionsAsync(
        IReadOnlyList<Indexer> indexers,
        CancellationToken cancellationToken)
    {
        var definitionIds = indexers
            .Select(i => i.DefinitionId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();

        if (definitionIds.Length == 0)
        {
            return ReadOnlyDictionary<Guid, string>.Empty;
        }

        var documents = await dbContext.IndexerDefinitions
            .AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id))
            .Select(d => new { d.Id, d.RawContent })
            .ToListAsync(cancellationToken);

        return documents.ToDictionary(d => d.Id, d => d.RawContent);
    }

    private async Task<(int Priority, Guid IndexerId, IReadOnlyList<ReleaseCandidate> Candidates)> SearchOneAsync(
        Indexer indexer,
        IReadOnlyDictionary<Guid, string> definitions,
        SearchCriterion criterion,
        CancellationToken cancellationToken)
    {
        // DefinitionId is load-bearing and not decoration: the definition adapter refuses to run
        // without it, and omitting it here made every definition-backed indexer throw into the catch
        // below — a silent zero-result on every search.
        var summary = new IndexerSummary(
            new IndexerId(indexer.Id), indexer.Name, indexer.Protocol, indexer.BaseUrl, indexer.Priority,
            indexer.Enabled, indexer.ToCapabilities(),
            indexer.DefinitionId is Guid definitionId ? new IndexerDefinitionId(definitionId) : null,
            Settings: indexer.ToSettings(), CatalogKey: indexer.CatalogKey, CatalogVersion: indexer.CatalogVersion,
            CatalogSourceId: indexer.CatalogSourceId is Guid sourceId ? new IndexerCatalogSourceId(sourceId) : null);

        // Resolved above, before the fan-out; null both when this indexer needs no definition and
        // when the one it names has been deleted, which the adapter tells apart from DefinitionId.
        var definitionContent = indexer.DefinitionId is Guid id && definitions.TryGetValue(id, out var raw)
            ? raw
            : null;

        // Decrypted here and passed alongside the summary rather than on it: the summary is the same
        // record the administration endpoint serializes. An unreadable row resolves to null and the
        // indexer is queried unauthenticated, which is the same degraded behaviour as having no
        // credential at all — never a failed search.
        var credential = credentials.TryDecrypt(indexer) is { } secret
            ? new IndexerCredential(indexer.CredentialUsername, secret)
            : null;

        // One span per indexer, so a search that took thirty seconds shows which endpoint spent them.
        // Client kind: this is an outbound call, even though the transport is chosen by the adapter.
        using var activity = CinomniTelemetry.Source.StartActivity("indexer.search", ActivityKind.Client);
        activity?.SetTag(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Discovery);
        activity?.SetTag(CinomniTelemetry.Tags.IndexerName, indexer.Name);

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var found = await indexerClient.SearchAsync(
                summary, credential, definitionContent, criterion, cancellationToken);

            // Shaped to fit the columns here, per indexer and before dedup, because the whole search
            // is persisted in one insert: a single oversized value from one feed used to fail that
            // insert for every indexer, and each retry spent every indexer's daily quota again.
            var storable = found.Select(ToStorable).OfType<ReleaseCandidate>().ToList();
            if (storable.Count < found.Count)
            {
                logger.LogWarning(
                    "Indexer {Indexer} returned {Dropped} release(s) whose download link cannot be stored intact; they were dropped.",
                    indexer.Name,
                    found.Count - storable.Count);
            }

            var candidates = storable
                .Where(candidate => candidate.Seeders is null
                    || candidate.Seeders >= indexer.MinimumSeeders.GetValueOrDefault())
                .ToList();
            DiscoveryMetrics.RecordSearch(indexer.Name, Stopwatch.GetElapsedTime(startedAt), candidates.Count);
            return (indexer.Priority, indexer.Id, candidates);
        }
        catch (IndexerQuotaExceededException exhausted)
        {
            DiscoveryMetrics.RecordSearchFailure(indexer.Name, Stopwatch.GetElapsedTime(startedAt));
            activity?.SetStatus(ActivityStatusCode.Error);
            logger.LogWarning(
                "Indexer {Indexer} search skipped because its request quota is exhausted ({FailureCode}).",
                indexer.Name,
                exhausted.Code);
            return (indexer.Priority, indexer.Id, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A swallowed failure is the whole problem this measures: downstream, a rejected query and
            // a genuine no-results are indistinguishable, so an indexer that has been broken for a week
            // looks exactly like a title nobody seeds.
            DiscoveryMetrics.RecordSearchFailure(indexer.Name, Stopwatch.GetElapsedTime(startedAt));
            // The status word travels; the exception does not. Its message quotes the request URL, and
            // that URL carries the account's API key.
            activity?.SetStatus(ActivityStatusCode.Error);

            // A rejected query and a genuine no-results are indistinguishable downstream, so the
            // query shape is logged with the failure: it is the only clue that an endpoint does not
            // speak the mode we asked it (configure its capabilities to fall back to t=search).
            logger.LogWarning(
                "Indexer {Indexer} search failed; skipping it. kind={ContentKind} season={Season} episode={Episode} tvdbId={TvdbId}",
                indexer.Name, criterion.ContentKind, criterion.SeasonNumber, criterion.EpisodeNumber, criterion.TvdbId);
            return (indexer.Priority, indexer.Id, []);
        }
    }

    /// <summary>
    /// Two-level dedup by release guid: distinct within one indexer, then across indexers the
    /// candidate from the highest-priority indexer (lowest priority value) wins. Each winner keeps the
    /// id of the indexer that returned it, which is what a result is attributed to once stored.
    /// </summary>
    private static List<(Guid IndexerId, ReleaseCandidate Candidate)> Deduplicate(
        IReadOnlyList<(int Priority, Guid IndexerId, IReadOnlyList<ReleaseCandidate> Candidates)> perIndexer) =>
        perIndexer
            .SelectMany(x => x.Candidates
                .DistinctBy(c => c.Guid)
                .Select(c => (x.Priority, x.IndexerId, Candidate: c)))
            .GroupBy(t => t.Candidate.Guid)
            .Select(g => g.OrderBy(t => t.Priority).Select(t => (t.IndexerId, t.Candidate)).First())
            .ToList();

    /// <summary>
    /// Makes an indexer's candidate storable in <c>search_results</c>, or rejects it. The feed is
    /// hostile input, so nothing about its lengths or characters can be assumed.
    /// <list type="bullet">
    /// <item>The download link is dropped with the candidate when it does not fit or carries a NUL or half a character:
    /// a clipped magnet or URL is a different download, and every module downstream would fetch it.</item>
    /// <item>The guid is clipped, the same width Decision clips it to for its evaluations, blocks and
    /// exclusions, so the release keeps one identity everywhere. It is clipped before dedup, so the
    /// returned candidates are the ones <see cref="GetResultsAsync"/> reads back.</item>
    /// <item>Everything else is only described, so it is clipped. NULs go everywhere: PostgreSQL
    /// refuses them in text, and a JSON feed can carry them.</item>
    /// </list>
    /// </summary>
    internal static ReleaseCandidate? ToStorable(ReleaseCandidate candidate)
    {
        var downloadUrl = candidate.DownloadUrl;
        if (string.IsNullOrEmpty(downloadUrl)
            || downloadUrl.Length > SearchResult.DownloadUrlMaxLength
            || downloadUrl.Contains('\0', StringComparison.Ordinal)
            || HasLoneSurrogate(downloadUrl))
        {
            return null;
        }

        return candidate with
        {
            Guid = Fit(candidate.Guid, SearchResult.ReleaseGuidMaxLength)!,
            Title = Fit(candidate.Title, SearchResult.TitleMaxLength)!,
            TvdbId = Fit(candidate.TvdbId, SearchResult.TvdbIdMaxLength),
            Category = Fit(candidate.Category, SearchResult.CategoryMaxLength),
            // A negative count is not a count. Zero, not unknown: a size of zero is refused downstream as
            // too small, and zero seeders still fail an indexer's minimum — reading them as unknown would
            // let a nonsensical figure through a minimum that a missing one does not.
            SizeBytes = Math.Max(0, candidate.SizeBytes),
            Seeders = candidate.Seeders is < 0 ? 0 : candidate.Seeders,
            Leechers = candidate.Leechers is < 0 ? 0 : candidate.Leechers,
        };
    }

    private static string? Fit(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var clean = value.Contains('\0', StringComparison.Ordinal)
            ? value.Replace("\0", string.Empty, StringComparison.Ordinal)
            : value;
        if (clean.Length > maxLength)
        {
            // Never between the two halves of a surrogate pair: half a character is not valid UTF-8,
            // and the encoder refuses it — the whole-search insert failure all over again.
            var cut = char.IsHighSurrogate(clean[maxLength - 1]) ? maxLength - 1 : maxLength;
            clean = clean[..cut];
        }

        return HasLoneSurrogate(clean) ? ReplaceLoneSurrogates(clean) : clean;
    }

    /// <summary>A surrogate without its partner, which a JSON feed can spell as <c>\ud800</c>.</summary>
    private static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReplaceLoneSurrogates(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '�';
            }
        }

        return new string(chars);
    }
}
