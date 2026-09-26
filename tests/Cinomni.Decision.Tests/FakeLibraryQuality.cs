using Cinomni.Library.Contracts;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Stands in for the library while deciding. It answers the one question Decision asks it — what is
/// already on disk for a unit — and refuses every other read, so a test that starts depending on more
/// of Library fails loudly instead of quietly widening the coupling between the two modules.
/// </summary>
internal sealed class FakeLibraryQuality(IReadOnlyDictionary<Guid, ReleaseQuality>? onDisk = null) : ILibraryQuery
{
    private readonly Dictionary<Guid, ReleaseQuality> _onDisk = onDisk is null ? [] : new(onDisk);

    /// <summary>Puts a quality on disk for a unit, as an import would have.</summary>
    public FakeLibraryQuality Holding(Guid unitId, ReleaseQuality quality)
    {
        _onDisk[unitId] = quality;
        return this;
    }

    public Task<ReleaseQuality?> GetCurrentQualityAsync(Guid unitId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_onDisk.TryGetValue(unitId, out var quality) ? quality : null);

    public Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<MediaAssetDetail?> GetAsync(MediaAssetId id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MediaAssetSummary>> GetByWorkAsync(Guid workId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MediaAssetSummary>> GetByUnitAsync(Guid unitId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MediaAssetSummary>> GetByUnitsAsync(
        IReadOnlyList<Guid> unitIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MediaVersionPath>> ListActiveVersionPathsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<MediaAssetSummary?> FindActiveByPathAsync(string fullPath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> FindActiveWorksUnderAsync(string directory, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
