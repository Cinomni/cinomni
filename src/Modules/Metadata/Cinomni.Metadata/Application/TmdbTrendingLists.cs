using System.Text.Json;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Application;

/// <summary>
/// TMDB's weekly trending list, behind <see cref="IMetadataLists"/>. The HTTP call stays in this
/// module; Catalog only sees the neutral titles.
/// </summary>
public sealed class TmdbTrendingLists(
    TmdbMetadataSource source,
    ILogger<TmdbTrendingLists> logger) : IMetadataLists
{
    public async Task<IReadOnlyList<TrendingTitle>> TrendingAsync(
        MetadataMediaKind kind,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (MetadataMediaKind.Movie or MetadataMediaKind.Series) || limit < 1)
        {
            return [];
        }

        if (!source.IsAvailable)
        {
            source.AnnounceUnavailable();
            return [];
        }

        try
        {
            using var document = await source.GetTrendingDocumentAsync(kind, cancellationToken);
            return document is null ? [] : TmdbTrendingParser.Parse(document.RootElement, kind, limit);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TMDB trending failed for {Kind}.", kind);
            return [];
        }
    }
}
