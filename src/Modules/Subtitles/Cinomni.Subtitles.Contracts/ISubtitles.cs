namespace Cinomni.Subtitles.Contracts;

/// <summary>
/// Write surface of the Subtitles module. Searching for a language on an asset is idempotent: keyed
/// by (asset, language, forced, hi), a redelivered trigger reuses the existing search rather than
/// forking it.
/// </summary>
public interface ISubtitleSearch
{
    /// <summary>
    /// Finds the missing subtitle languages for an asset (profile requirements minus what it already
    /// has) and, for each, searches providers, downloads the best candidate over the threshold, writes
    /// it next to the video and registers it — emitting <c>SubtitleAvailable</c>. No-op for an unknown
    /// asset or a language already satisfied.
    /// </summary>
    Task SearchForAssetAsync(Guid assetId, CancellationToken cancellationToken = default);
}

/// <summary>Read model of subtitle searches and the subtitles obtained.</summary>
public interface ISubtitleQuery
{
    Task<IReadOnlyList<SubtitleSearchSummary>> ListForAssetAsync(Guid assetId, CancellationToken cancellationToken = default);

    Task<SubtitleSearchDetail?> GetAsync(SubtitleSearchId searchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The subtitle files obtained for an asset, with where each one sits — what a player needs to
    /// offer them as tracks. Unscoped like the rest of this read model: the caller checks that the
    /// viewer may see the asset before serving anything from it.
    /// </summary>
    Task<IReadOnlyList<SubtitleAssetSummary>> ListObtainedAsync(Guid assetId, CancellationToken cancellationToken = default);
}
