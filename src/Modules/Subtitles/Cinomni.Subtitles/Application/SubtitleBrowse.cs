using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Cinomni.Library.Contracts;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Application;

/// <summary>
/// Subtitle searches as one person sees them: a search is visible when the media asset it belongs to is,
/// which is to say when that asset's work is. Subtitles knows about neither collections nor works — it
/// resolves the asset through Library (→i) and asks Catalog's content access (→i), the single authority.
/// <para>
/// A concrete class rather than a published interface: only this module's endpoints bind it. The unscoped
/// <see cref="ISubtitleQuery"/> stays for the event and command handlers that drive the search itself.
/// </para>
/// </summary>
public sealed class SubtitleBrowse(ISubtitleQuery searches, ILibraryQuery library, IContentAccess access)
{
    public async Task<IReadOnlyList<SubtitleSearchSummary>> ListForAssetAsync(
        Viewer viewer,
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        await CanSeeAssetAsync(viewer, assetId, cancellationToken)
            ? await searches.ListForAssetAsync(assetId, cancellationToken)
            : [];

    public async Task<SubtitleSearchDetail?> GetAsync(
        Viewer viewer,
        SubtitleSearchId searchId,
        CancellationToken cancellationToken = default)
    {
        var detail = await searches.GetAsync(searchId, cancellationToken);

        // Not yours reads exactly like not found.
        return detail is not null && await CanSeeAssetAsync(viewer, detail.Search.AssetId, cancellationToken)
            ? detail
            : null;
    }

    /// <summary>An asset that does not exist answers false, so an unknown id is as opaque as a hidden one.</summary>
    private async Task<bool> CanSeeAssetAsync(Viewer viewer, Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await library.GetAsync(new MediaAssetId(assetId), cancellationToken);
        return asset is not null && await access.CanSeeWorkAsync(viewer, asset.Asset.WorkId, cancellationToken);
    }
}
