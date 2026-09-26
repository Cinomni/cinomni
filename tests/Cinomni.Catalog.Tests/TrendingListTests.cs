using System.Text.Json;
using Cinomni.Catalog.Application;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

public sealed class TrendingListTests : IAsyncLifetime
{
    private readonly ScriptedLists _lists = new();
    private readonly RecordingRefresh _refresh = new();
    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await CatalogTestHost.CreateAsync("cinomni_test_catalog_trending", services =>
        {
            services.AddSingleton<IMetadataLists>(_lists);
            services.AddSingleton<IMetadataRefresh>(_refresh);
            services.AddSingleton<ILiveOptions<TrendingListOptions>>(new FixedTrending(true));
        });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_new_title_is_added_once_and_a_known_title_is_not_added_again()
    {
        _lists.Movies.Add(new TrendingTitle("tmdb", "11", "Inception", 2010, MetadataMediaKind.Movie));

        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TrendingListRefresh>().RunAsync();
        }

        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TrendingListRefresh>().RunAsync();
        }

        await using var read = _host.CreateAsyncScope();
        var works = await read.ServiceProvider.GetRequiredService<ICatalogQuery>().ListAsync();
        var inception = Assert.Single(works, w => w.Title == "Inception");
        var entry = Assert.Single(await read.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .ImportListEntries.AsNoTracking().ToListAsync());
        Assert.Equal(TrendingListRefresh.Added, entry.Outcome);
        Assert.Equal(inception.Id.Value, entry.WorkId);

        // Enrichment is asked on every run (a no-op within the refresh window), so a failure on the run
        // that added the work is retried by the next one.
        Assert.Equal(2, _refresh.WorkIds.Count);
        Assert.All(_refresh.WorkIds, id => Assert.Equal(inception.Id.Value, id));
    }

    [Fact]
    public async Task A_listed_title_is_catalogued_unwatched()
    {
        _lists.Movies.Add(new TrendingTitle("tmdb", "27205", "Suggestion", 2010, MetadataMediaKind.Movie));

        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TrendingListRefresh>().RunAsync();
        }

        await using var read = _host.CreateAsyncScope();
        var payload = await read.ServiceProvider.GetRequiredService<OperationsDbContext>().Outbox
            .AsNoTracking()
            .Where(m => m.EventType == CatalogEventNames.WorkAdded)
            .Select(m => m.Payload)
            .SingleAsync();
        var added = JsonSerializer.Deserialize<WorkAdded>(payload, JsonSerializerOptions.Web)!;
        Assert.Equal("Suggestion", added.Title);
        Assert.False(added.Monitored);
    }

    [Fact]
    public async Task A_failed_enrichment_leaves_the_entry_written_the_rest_of_the_list_done_and_is_retried()
    {
        _lists.Movies.Add(new TrendingTitle("tmdb", "1", "First", 2020, MetadataMediaKind.Movie));
        _lists.Movies.Add(new TrendingTitle("tmdb", "2", "Second", 2021, MetadataMediaKind.Movie));
        _refresh.FailingExternalIds.Add("1");

        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TrendingListRefresh>().RunAsync();
        }

        Guid first;
        await using (var read = _host.CreateAsyncScope())
        {
            var entries = await read.ServiceProvider.GetRequiredService<CatalogDbContext>()
                .ImportListEntries.AsNoTracking().ToListAsync();
            Assert.Equal(["1", "2"], entries.Select(e => e.ExternalId).Order());
            Assert.All(entries, e => Assert.Equal(TrendingListRefresh.Added, e.Outcome));
            first = entries.Single(e => e.ExternalId == "1").WorkId!.Value;
        }

        Assert.DoesNotContain(first, _refresh.WorkIds);

        // The provider is back: the next run enriches the work the failed run left bare.
        _refresh.FailingExternalIds.Clear();
        await using (var scope = _host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TrendingListRefresh>().RunAsync();
        }

        Assert.Contains(first, _refresh.WorkIds);
    }

    [Fact]
    public async Task An_off_list_adds_nothing()
    {
        await using var host = await CatalogTestHost.CreateAsync("cinomni_test_catalog_trending_off", services =>
        {
            services.AddSingleton<IMetadataLists>(_lists);
            services.AddSingleton<ILiveOptions<TrendingListOptions>>(new FixedTrending(false));
        });
        _lists.Movies.Add(new TrendingTitle("tmdb", "99", "Skipped", 2020, MetadataMediaKind.Movie));

        await using var scope = host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TrendingListRefresh>().RunAsync();

        var works = await scope.ServiceProvider.GetRequiredService<ICatalogQuery>().ListAsync();
        Assert.DoesNotContain(works, w => w.Title == "Skipped");
    }

    private sealed class ScriptedLists : IMetadataLists
    {
        public List<TrendingTitle> Movies { get; } = [];

        public Task<IReadOnlyList<TrendingTitle>> TrendingAsync(
            MetadataMediaKind kind, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrendingTitle>>(kind == MetadataMediaKind.Movie ? Movies : []);
    }

    private sealed class RecordingRefresh : IMetadataRefresh
    {
        public List<Guid> WorkIds { get; } = [];

        public HashSet<string> FailingExternalIds { get; } = [];

        public Task RefreshAsync(
            Guid workId, string provider, string externalId, MetadataMediaKind kind = MetadataMediaKind.Movie,
            CancellationToken cancellationToken = default)
        {
            if (FailingExternalIds.Contains(externalId))
            {
                throw new InvalidOperationException("provider down");
            }

            WorkIds.Add(workId);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTrending(bool enabled) : ILiveOptions<TrendingListOptions>
    {
        public TrendingListOptions Current { get; } = new() { Enabled = enabled };
    }
}
