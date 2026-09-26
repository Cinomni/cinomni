using Cinomni.Metadata.Contracts;

namespace Cinomni.Requests.Tests;

/// <summary>
/// Stands in for the Metadata module: records the enrichment triggers a fulfilled request fires, without
/// touching a provider. Can be made to throw, so the "enrichment is out-of-process" path is covered.
/// </summary>
internal sealed class FakeMetadataRefresh : IMetadataRefresh
{
    public List<(Guid WorkId, string Provider, string ExternalId, MetadataMediaKind Kind)> Refreshed { get; } = [];

    public bool Fail { get; set; }

    public Task RefreshAsync(
        Guid workId,
        string provider,
        string externalId,
        MetadataMediaKind kind = MetadataMediaKind.Movie,
        CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new HttpRequestException("The provider is unreachable.");
        }

        Refreshed.Add((workId, provider, externalId, kind));
        return Task.CompletedTask;
    }
}
