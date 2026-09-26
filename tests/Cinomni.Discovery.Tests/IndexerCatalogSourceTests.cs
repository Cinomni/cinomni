using Cinomni.Discovery.Application;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Catalog sources against real PostgreSQL: the snapshot survives a failed refresh, removing a source
/// never removes what was installed from it, install stays idempotent per (source, key), and the
/// settings rule reads the snapshot rather than anything compiled into Cinomni.
/// </summary>
public sealed class IndexerCatalogSourceTests : IAsyncLifetime
{
    private readonly FakeCatalogFetcher _fetcher = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_catalog_sources", services =>
        {
            services.AddSingleton<FakeIndexerCatalog>();
            services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
            services.AddSingleton<IIndexerCatalogFetcher>(_fetcher);
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Cinomni_ships_no_source_and_no_entry()
    {
        await using var scope = _provider.CreateAsyncScope();

        Assert.Empty(await Sources(scope).ListSourcesAsync());
        Assert.Empty(await Admin(scope).ListCatalogAsync());
    }

    [Fact]
    public async Task Adding_a_source_refreshes_it_and_lists_its_entries_with_their_source()
    {
        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(
            CatalogManifests.Entry("alpha"), CatalogManifests.Entry("beta", requiresFlareSolverr: true)));
        await using var scope = _provider.CreateAsyncScope();

        var added = await Sources(scope).AddSourceAsync("  Example  ", CatalogManifests.SourceUrl);

        Assert.True(added.IsSuccess);
        Assert.Equal("Example", added.Value.Name);
        Assert.True(added.Value.LastRefreshSucceeded);
        Assert.Equal(IndexerCatalogSources.RefreshedCode, added.Value.LastRefreshCode);
        Assert.Equal(2, added.Value.EntryCount);
        var catalog = await Admin(scope).ListCatalogAsync();
        Assert.Equal(["alpha", "beta"], catalog.Select(entry => entry.Key));
        Assert.All(catalog, entry =>
        {
            Assert.Equal(added.Value.Id, entry.SourceId);
            Assert.Equal("Example", entry.SourceName);
            Assert.Null(entry.InstalledIndexerId);
        });
    }

    [Fact]
    public async Task A_source_whose_first_refresh_fails_is_still_saved_with_the_failure()
    {
        _fetcher.Fail(CatalogManifests.SourceUrl, HttpIndexerCatalogFetcher.FetchFailedCode, "The source answered HTTP 503.");
        await using var scope = _provider.CreateAsyncScope();

        var added = await Sources(scope).AddSourceAsync("Down", CatalogManifests.SourceUrl);

        Assert.True(added.IsSuccess);
        Assert.False(added.Value.LastRefreshSucceeded);
        Assert.Equal(HttpIndexerCatalogFetcher.FetchFailedCode, added.Value.LastRefreshCode);
        Assert.Equal("The source answered HTTP 503.", added.Value.LastRefreshMessage);
        Assert.NotNull(added.Value.LastRefreshedAt);
        Assert.Equal(0, added.Value.EntryCount);
        Assert.Single(await Sources(scope).ListSourcesAsync());
    }

    [Fact]
    public async Task A_failed_or_invalid_refresh_keeps_the_previous_snapshot()
    {
        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(CatalogManifests.Entry("alpha")));
        await using var scope = _provider.CreateAsyncScope();
        var sources = Sources(scope);
        var added = await sources.AddSourceAsync("Example", CatalogManifests.SourceUrl);

        _fetcher.Fail(CatalogManifests.SourceUrl, HttpIndexerCatalogFetcher.TooLargeCode, "The manifest is larger than 4 MiB.");
        var tooLarge = await sources.RefreshSourceAsync(added.Value.Id);
        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(
            CatalogManifests.Entry("alpha"), CatalogManifests.Entry("private", definition: CatalogManifests.DefinitionWithLogin)));
        var invalid = await sources.RefreshSourceAsync(added.Value.Id);
        _fetcher.Serve(CatalogManifests.SourceUrl, "{ this is not json");
        var badJson = await sources.RefreshSourceAsync(added.Value.Id);

        Assert.Equal(HttpIndexerCatalogFetcher.TooLargeCode, tooLarge.Value.LastRefreshCode);
        Assert.Equal(IndexerCatalogManifest.InvalidManifestCode, invalid.Value.LastRefreshCode);
        Assert.Equal(IndexerCatalogManifest.InvalidManifestCode, badJson.Value.LastRefreshCode);
        Assert.All(new[] { tooLarge, invalid, badJson }, outcome =>
        {
            Assert.False(outcome.Value.LastRefreshSucceeded);
            Assert.Equal(1, outcome.Value.EntryCount);
        });
        Assert.Equal("alpha", Assert.Single(await Admin(scope).ListCatalogAsync()).Key);
    }

    [Fact]
    public async Task A_successful_refresh_replaces_the_snapshot_even_twice_in_one_scope()
    {
        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(CatalogManifests.Entry("alpha")));
        await using var scope = _provider.CreateAsyncScope();
        var sources = Sources(scope);
        var added = await sources.AddSourceAsync("Example", CatalogManifests.SourceUrl);

        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(
            CatalogManifests.Entry("alpha", version: 2), CatalogManifests.Entry("gamma")));
        await sources.RefreshSourceAsync(added.Value.Id);
        var again = await sources.RefreshSourceAsync(added.Value.Id);

        Assert.True(again.Value.LastRefreshSucceeded);
        var catalog = await Admin(scope).ListCatalogAsync();
        Assert.Equal(["alpha", "gamma"], catalog.Select(entry => entry.Key));
        Assert.Equal(2, catalog[0].Version);
    }

    [Fact]
    public async Task Concurrent_refreshes_of_one_source_serialize_on_its_row()
    {
        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(
            CatalogManifests.Entry("alpha"), CatalogManifests.Entry("beta")));
        IndexerCatalogSourceId id;
        await using (var scope = _provider.CreateAsyncScope())
        {
            id = (await Sources(scope).AddSourceAsync("Example", CatalogManifests.SourceUrl)).Value.Id;
        }

        var refreshes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await Sources(scope).RefreshSourceAsync(id);
        }));

        Assert.All(refreshes, refresh => Assert.True(refresh.Value.LastRefreshSucceeded));
        await using var verify = _provider.CreateAsyncScope();
        Assert.Equal(2, await Db(verify).IndexerCatalogEntries.CountAsync());
    }

    [Theory]
    [InlineData("http://catalog.example.org/indexers.json")]
    [InlineData("https://127.0.0.1/indexers.json")]
    [InlineData("https://10.1.2.3/indexers.json")]
    [InlineData("https://[fd00::1]/indexers.json")]
    [InlineData("https://user:pass@catalog.example.org/indexers.json")]
    [InlineData("https://catalog.example.org/indexers.json#fragment")]
    [InlineData("catalog.example.org/indexers.json")]
    [InlineData("")]
    public async Task A_url_that_is_not_public_https_is_refused_and_nothing_is_fetched(string url)
    {
        await using var scope = _provider.CreateAsyncScope();

        var added = await Sources(scope).AddSourceAsync("Bad", url);

        Assert.Equal(IndexerCatalogSources.InvalidUrlCode, added.Error.Code);
        Assert.Empty(_fetcher.Requests);
        Assert.Empty(await Sources(scope).ListSourcesAsync());
    }

    [Fact]
    public async Task A_second_subscription_to_the_same_url_is_refused()
    {
        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest());
        await using var scope = _provider.CreateAsyncScope();
        await Sources(scope).AddSourceAsync("First", CatalogManifests.SourceUrl);

        var duplicate = await Sources(scope).AddSourceAsync("Second", CatalogManifests.SourceUrl);

        Assert.Equal(IndexerCatalogSources.DuplicateUrlCode, duplicate.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public async Task A_bad_source_name_is_refused(string name)
    {
        await using var scope = _provider.CreateAsyncScope();

        var added = await Sources(scope).AddSourceAsync(name, CatalogManifests.SourceUrl);

        Assert.Equal(IndexerCatalogSources.InvalidNameCode, added.Error.Code);
        Assert.Equal(IndexerCatalogSources.InvalidNameCode,
            (await Sources(scope).AddSourceAsync(new string('n', 201), CatalogManifests.SourceUrl)).Error.Code);
    }

    [Fact]
    public async Task Install_is_idempotent_and_snapshots_definition_and_defaults()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha", requiresFlareSolverr: true));
        await using var scope = _provider.CreateAsyncScope();
        var admin = Admin(scope);

        var first = await admin.InstallCatalogIndexerAsync(source, "alpha", null, null, null, null);
        var second = await admin.InstallCatalogIndexerAsync(source, "ALPHA", null, null, null, null);

        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        var db = Db(scope);
        var indexer = await db.Indexers.SingleAsync();
        Assert.Equal(source.Value, indexer.CatalogSourceId);
        Assert.Equal("alpha", indexer.CatalogKey);
        Assert.Equal(1, indexer.CatalogVersion);
        Assert.Equal(20, indexer.Priority);
        Assert.Equal(1, indexer.MinimumSeeders);
        Assert.True(indexer.PreferMagnet);
        Assert.True(indexer.UseFlareSolverr);
        Assert.Equal("https://indexer.example/", indexer.BaseUrl);
        var raw = await db.IndexerDefinitions.Where(d => d.Id == indexer.DefinitionId).Select(d => d.RawContent).SingleAsync();
        Assert.Equal(
            (await db.IndexerCatalogEntries.SingleAsync()).RawDefinition, raw);
        Assert.Equal(first.Value, Assert.Single(await admin.ListCatalogAsync()).InstalledIndexerId);
        Assert.Equal(source, Assert.Single(await admin.ListIndexersAsync()).CatalogSourceId);
    }

    [Fact]
    public async Task Concurrent_install_creates_one_indexer_and_one_definition()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha"));

        var installs = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await Admin(scope).InstallCatalogIndexerAsync(source, "alpha", null, null, null, null);
        }));

        Assert.All(installs, result => Assert.True(result.IsSuccess));
        Assert.Single(installs.Select(result => result.Value).Distinct());
        await using var verify = _provider.CreateAsyncScope();
        Assert.Equal(1, await Db(verify).Indexers.CountAsync());
        Assert.Equal(1, await Db(verify).IndexerDefinitions.CountAsync());
    }

    [Fact]
    public async Task Two_sources_may_publish_the_same_key_and_each_installs_its_own()
    {
        var first = await AddSourceAsync(CatalogManifests.SourceUrl, CatalogManifests.Entry("shared"));
        var second = await AddSourceAsync(CatalogManifests.OtherSourceUrl, CatalogManifests.Entry("shared", "https://other.indexer.example/"));
        await using var scope = _provider.CreateAsyncScope();

        var a = await Admin(scope).InstallCatalogIndexerAsync(first, "shared", null, null, null, null);
        var b = await Admin(scope).InstallCatalogIndexerAsync(second, "shared", null, null, null, null);

        Assert.NotEqual(a.Value, b.Value);
        Assert.Equal(2, (await Admin(scope).ListCatalogAsync()).Count(entry => entry.InstalledIndexerId is not null));
    }

    [Fact]
    public async Task Install_and_test_refuse_an_unknown_source_key_or_a_disabled_source()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha"));
        await using var scope = _provider.CreateAsyncScope();
        var admin = Admin(scope);

        var unknownSource = await admin.InstallCatalogIndexerAsync(IndexerCatalogSourceId.New(), "alpha", null, null, null, null);
        var unknownKey = await admin.TestCatalogIndexerAsync(source, "missing", null, null, null, null);
        await Sources(scope).UpdateSourceAsync(source, "Example", enabled: false);
        var disabled = await admin.InstallCatalogIndexerAsync(source, "alpha", null, null, null, null);

        Assert.Equal(IndexerCatalogSources.NotFoundCode, unknownSource.Error.Code);
        Assert.Equal("discovery.catalog.not_found", unknownKey.Error.Code);
        Assert.Equal("discovery.catalog.not_found", disabled.Error.Code);
        Assert.Empty(await admin.ListCatalogAsync());
        Assert.Empty(await Db(scope).Indexers.ToListAsync());
    }

    [Fact]
    public async Task A_base_url_outside_the_entry_or_a_required_browser_turned_off_is_refused()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha", requiresFlareSolverr: true));
        await using var scope = _provider.CreateAsyncScope();
        var admin = Admin(scope);

        var elsewhere = await admin.InstallCatalogIndexerAsync(source, "alpha", null, "https://elsewhere.example/", null, null);
        var noBrowser = await admin.InstallCatalogIndexerAsync(source, "alpha", null, null, null,
            new IndexerSettings(1, true, null, null, IndexerLimitsUnit.Day, false));
        var priority = await admin.InstallCatalogIndexerAsync(source, "alpha", null, null, 51, null);

        Assert.Equal("discovery.catalog.unsupported_base_url", elsewhere.Error.Code);
        Assert.Equal("discovery.catalog.browser_required", noBrowser.Error.Code);
        Assert.Equal("discovery.invalid_priority", priority.Error.Code);
        Assert.Empty(await Db(scope).Indexers.ToListAsync());
    }

    [Fact]
    public async Task Live_test_does_not_install()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha"));
        await using var scope = _provider.CreateAsyncScope();

        var live = await Admin(scope).TestCatalogIndexerAsync(source, "alpha", null, null, null, null);

        Assert.True(live.Value.Succeeded);
        Assert.Empty(await Db(scope).Indexers.ToListAsync());
    }

    [Fact]
    public async Task Removing_a_source_leaves_its_installed_indexer_working()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha", requiresFlareSolverr: true));
        await using var scope = _provider.CreateAsyncScope();
        var admin = Admin(scope);
        var installed = await admin.InstallCatalogIndexerAsync(source, "alpha", null, null, null, null);

        var deleted = await Sources(scope).DeleteSourceAsync(source);

        Assert.True(deleted.IsSuccess);
        await using var verify = _provider.CreateAsyncScope();
        var db = Db(verify);
        Assert.Empty(await db.IndexerCatalogEntries.ToListAsync());
        var indexer = await db.Indexers.SingleAsync();
        Assert.Equal(installed.Value.Value, indexer.Id);
        Assert.Null(indexer.CatalogSourceId);
        Assert.Equal("alpha", indexer.CatalogKey);
        Assert.True(await db.IndexerDefinitions.AnyAsync(d => d.Id == indexer.DefinitionId));
        var summary = Assert.Single(await Admin(verify).ListIndexersAsync());
        Assert.True(summary.Enabled);
        Assert.Null(summary.CatalogSourceId);
        Assert.True((await Admin(verify).TestIndexerAsync(installed.Value)).Value.Succeeded);
        // No snapshot to resolve against any more: a plain definition indexer, the choice is the operator's.
        Assert.True((await Admin(verify).SetSettingsAsync(installed.Value, new IndexerSettings())).IsSuccess);
        Assert.Equal(IndexerCatalogSources.NotFoundCode, (await Sources(verify).DeleteSourceAsync(source)).Error.Code);
    }

    [Fact]
    public async Task Settings_resolve_the_browser_requirement_against_the_source_snapshot()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha", requiresFlareSolverr: true));
        await using var scope = _provider.CreateAsyncScope();
        var installed = await Admin(scope).InstallCatalogIndexerAsync(source, "alpha", null, null, null, null);

        var refused = await Admin(scope).SetSettingsAsync(installed.Value, new IndexerSettings(UseFlareSolverr: false));

        Assert.Equal("discovery.catalog.browser_required", refused.Error.Code);

        // The source drops the requirement; the next refresh is what the rule reads.
        _fetcher.Serve(CatalogManifests.SourceUrl, CatalogManifests.Manifest(CatalogManifests.Entry("alpha", version: 2)));
        await Sources(scope).RefreshSourceAsync(source);
        Assert.True((await Admin(scope).SetSettingsAsync(installed.Value, new IndexerSettings(UseFlareSolverr: false))).IsSuccess);
    }

    [Fact]
    public async Task Renaming_and_disabling_a_source_is_reported_back()
    {
        var source = await AddSourceAsync(CatalogManifests.Entry("alpha"));
        await using var scope = _provider.CreateAsyncScope();

        var updated = await Sources(scope).UpdateSourceAsync(source, "Renamed", enabled: false);
        var missing = await Sources(scope).UpdateSourceAsync(IndexerCatalogSourceId.New(), "x", enabled: true);

        Assert.Equal("Renamed", updated.Value.Name);
        Assert.False(updated.Value.Enabled);
        Assert.Equal(1, updated.Value.EntryCount);
        Assert.Equal(IndexerCatalogSources.NotFoundCode, missing.Error.Code);
    }

    [Fact]
    public async Task The_real_fetcher_refuses_a_source_host_that_resolves_to_loopback()
    {
        // A composition without the fake: the module's own registration, over the Kernel SSRF handler.
        await using var provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_catalog_sources_ssrf");
        await using var scope = provider.CreateAsyncScope();

        var added = await scope.ServiceProvider.GetRequiredService<IIndexerCatalogSources>()
            .AddSourceAsync("Loopback", "https://localhost:9/indexers.json");

        Assert.True(added.IsSuccess);
        Assert.False(added.Value.LastRefreshSucceeded);
        Assert.Equal(HttpIndexerCatalogFetcher.FetchFailedCode, added.Value.LastRefreshCode);
        Assert.Equal(0, added.Value.EntryCount);
    }

    private Task<IndexerCatalogSourceId> AddSourceAsync(params Dictionary<string, object?>[] entries) =>
        AddSourceAsync(CatalogManifests.SourceUrl, entries);

    private async Task<IndexerCatalogSourceId> AddSourceAsync(string url, params Dictionary<string, object?>[] entries)
    {
        _fetcher.Serve(url, CatalogManifests.Manifest(entries));
        await using var scope = _provider.CreateAsyncScope();
        var added = await Sources(scope).AddSourceAsync("Example", url);
        Assert.True(added.Value.LastRefreshSucceeded, added.Value.LastRefreshMessage);
        return added.Value.Id;
    }

    private static IIndexerCatalogSources Sources(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IIndexerCatalogSources>();

    private static IIndexerAdministration Admin(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

    private static DiscoveryDbContext Db(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
}
