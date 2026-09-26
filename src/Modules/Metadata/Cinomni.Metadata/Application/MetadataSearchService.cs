using Cinomni.Kernel.Results;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Application;

/// <summary>
/// Searches the configured providers for content by term (behind the ACL) and returns neutral candidates
/// tagged with their origin provider. Only providers that support the requested media kind <i>and are
/// available</i> are queried, consulted in configured priority order; a provider that fails is skipped,
/// not fatal. A live query — nothing is persisted until a work is refreshed.
/// <para>
/// Availability is what separates an empty answer from no answer. A provider with no API key is not a
/// provider that found nothing; it is one that was never asked. When the requested kind has no available
/// provider left the search fails with <see cref="MetadataErrors.NoProvider"/> rather than succeeding
/// with an empty list, so the client can say "no provider is configured" instead of "no titles matched".
/// </para>
/// <para>
/// Candidates are then de-duplicated across providers (<see cref="MetadataCandidateMerge"/>): because the
/// providers are consulted in priority order, the highest-priority row survives a merge and inherits the
/// external ids the duplicates contributed. Without this a series search returns the same show once per
/// provider.
/// </para>
/// </summary>
public sealed class MetadataSearchService(
    IEnumerable<IMetadataSource> sources,
    MetadataOptions options,
    ILogger<MetadataSearchService> logger) : IMetadataSearch
{
    public async Task<Result<IReadOnlyList<MetadataCandidate>>> SearchAsync(
        string term,
        int? year,
        MetadataMediaKind kind = MetadataMediaKind.Movie,
        CancellationToken cancellationToken = default)
    {
        var query = new MetadataProviderQuery(term, year, kind);
        var capable = sources.Where(s => s.SupportedKinds.Contains(kind)).ToList();
        var ordered = capable
            .Where(s => s.IsAvailable)
            .OrderBy(s => options.PriorityOf(s.Name))
            .ToList();

        // Whoever is skipped gets to say why, once for the process and naming its own configuration key.
        // Skipping silently is what made an unconfigured installation indistinguishable from an empty
        // internet.
        var unavailable = capable
            .Where(s => !s.IsAvailable)
            .OrderBy(s => options.PriorityOf(s.Name))
            .ToList();
        foreach (var source in unavailable)
        {
            source.AnnounceUnavailable();
        }

        // Nobody to ask is not a miss. Returning an empty list here is what let a movie search on an
        // installation with no TMDB key read as "no titles matched" — the provider that would have
        // answered has already named its configuration key in the log, once, and the caller now gets a
        // failure it can present as such instead of an absence.
        if (ordered.Count == 0)
        {
            return Result<IReadOnlyList<MetadataCandidate>>.Failure(
                MetadataErrors.NoProviderAvailable(kind, unavailable.Select(s => s.Name).ToList()));
        }

        var results = new List<MetadataCandidate>();
        foreach (var source in ordered)
        {
            try
            {
                var candidates = await source.SearchAsync(query, cancellationToken);
                results.AddRange(candidates.Select(c => ToContract(source.Name, kind, c)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Metadata provider {Provider} search failed.", source.Name);
            }
        }

        // Sharing an external id proves two rows are the same work, so that merge is safe for any kind.
        // The title+year fallback is a heuristic and only runs for a series: that is where the pain is
        // (three providers, one show, three rows) and where an exact title+year collision between two
        // distinct works is least likely. A movie search keeps the behaviour it ships with today.
        return Result<IReadOnlyList<MetadataCandidate>>.Success(
            MetadataCandidateMerge.Merge(results, mergeByTitleAndYear: kind == MetadataMediaKind.Series));
    }

    private static MetadataCandidate ToContract(string provider, MetadataMediaKind kind, ProviderMetadataCandidate candidate)
    {
        var externalIds = candidate.ExternalIds ?? ProviderExternalIds.None;
        return new MetadataCandidate(
            provider,
            kind,
            candidate.ExternalId,
            candidate.Title,
            candidate.Year,
            candidate.Overview,
            externalIds.TvdbId,
            externalIds.ImdbId,
            externalIds.TmdbId);
    }
}
