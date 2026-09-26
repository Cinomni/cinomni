using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

public sealed class IndexerQuotaTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_quota", services =>
        {
            services.AddSingleton<FakeIndexerCatalog>();
            services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Concurrent_consumption_never_exceeds_limit_and_a_new_utc_day_rolls_over()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var added = await admin.AddIndexerAsync("Quota", IndexerProtocol.Torznab, "https://quota.example/", 1);
        var quota = scope.ServiceProvider.GetRequiredService<IIndexerQuota>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.IndexerUsages.Add(new IndexerUsage
        {
            IndexerId = added.Value.Value,
            UsageDate = today.AddDays(-1),
            QueryCount = 5,
        });
        await db.SaveChangesAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            quota.TryConsumeAsync(added.Value.Value, IndexerRequestKind.Query, 5, CancellationToken.None)));

        Assert.Equal(5, attempts.Count(consumed => consumed));
        Assert.Equal(5, (await db.IndexerUsages.SingleAsync(u => u.UsageDate == today)).QueryCount);
        Assert.Equal(2, await db.IndexerUsages.CountAsync());
    }
}
