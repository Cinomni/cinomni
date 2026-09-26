using Cinomni.Metadata.Contracts;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The refresh a composition has when Metadata is not registered. Adding a work still succeeds; the
/// snapshot simply waits until a later refresh, which is the same state a failed provider call leaves.
/// </summary>
public sealed class NoMetadataRefresh : IMetadataRefresh
{
    public Task RefreshAsync(
        Guid workId,
        string provider,
        string externalId,
        MetadataMediaKind kind = MetadataMediaKind.Movie,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
