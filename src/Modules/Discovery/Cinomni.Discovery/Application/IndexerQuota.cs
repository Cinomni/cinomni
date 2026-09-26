using Cinomni.Discovery.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Discovery.Application;

internal enum IndexerRequestKind
{
    Query,
    Grab,
}

internal interface IIndexerQuota
{
    Task<bool> TryConsumeAsync(
        Guid indexerId, IndexerRequestKind kind, int? limit, CancellationToken cancellationToken);
}

/// <summary>Atomically increments one UTC-day counter before an external request is attempted.</summary>
internal sealed class IndexerQuota(DiscoveryDbContext dbContext) : IIndexerQuota, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> TryConsumeAsync(
        Guid indexerId, IndexerRequestKind kind, int? limit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var date = DateOnly.FromDateTime(DateTime.UtcNow);
            var ceiling = limit ?? int.MaxValue;
            var sql = kind == IndexerRequestKind.Query
                ? """
                  INSERT INTO discovery.indexer_usage (indexer_id, usage_date, query_count, grab_count)
                  VALUES ({0}, {1}, 1, 0)
                  ON CONFLICT (indexer_id, usage_date) DO UPDATE
                  SET query_count = discovery.indexer_usage.query_count + 1
                  WHERE discovery.indexer_usage.query_count < {2}
                  """
                : """
                  INSERT INTO discovery.indexer_usage (indexer_id, usage_date, query_count, grab_count)
                  VALUES ({0}, {1}, 0, 1)
                  ON CONFLICT (indexer_id, usage_date) DO UPDATE
                  SET grab_count = discovery.indexer_usage.grab_count + 1
                  WHERE discovery.indexer_usage.grab_count < {2}
                  """;

            return await dbContext.Database.ExecuteSqlRawAsync(
                sql, [indexerId, date, ceiling], cancellationToken) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed class IndexerQuotaExceededException(IndexerRequestKind kind)
    : Exception($"Indexer {kind.ToString().ToLowerInvariant()} quota is exhausted.")
{
    public string Code => kind == IndexerRequestKind.Query
        ? "discovery.indexer.query_limit_exhausted"
        : "discovery.indexer.grab_limit_exhausted";
}
