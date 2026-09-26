using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Identifiers;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Integration tests for Discovery against a real PostgreSQL instance: federated search fans out
/// and deduplicates; internal base URLs are refused (SSRF); and a catalogued, monitored movie is
/// driven all the way to a persisted search and a SearchCompleted event across three modules.
/// </summary>
public sealed class DiscoverySearchTests : IAsyncLifetime
{
    private readonly FakeIndexerCatalog _indexers = new();
    private readonly CompletedSink _completed = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery", services =>
        {
            services.AddSingleton(_indexers);
            services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
            services.AddSingleton(_completed);
            services.AddScoped<IEventHandler<SearchCompleted>, SearchCompletedSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Search_fans_out_across_indexers_and_deduplicates_by_guid()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            Assert.True((await admin.AddIndexerAsync("Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1)).IsSuccess);
            Assert.True((await admin.AddIndexerAsync("Beta", IndexerProtocol.Torznab, "https://beta.example/torznab", 2)).IsSuccess);
        }

        _indexers.Set("Alpha", Candidate("shared", "Shared from Alpha", "Alpha"), Candidate("only-a", "Only A", "Alpha"));
        _indexers.Set("Beta", Candidate("shared", "Shared from Beta", "Beta"), Candidate("only-b", "Only B", "Beta"));

        SearchOutcome outcome;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            outcome = await search.SearchAsync(new SearchCriterion("Whatever", null, null, null, "Movie"));
        }

        // Three unique guids; the shared one is won by Alpha (lower priority value).
        Assert.Equal(3, outcome.Candidates.Count);
        var shared = Assert.Single(outcome.Candidates, c => c.Guid == "shared");
        Assert.Equal("Alpha", shared.IndexerName);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var execution = await db.SearchExecutions.SingleAsync();
            Assert.Equal(3, execution.ResultCount);
            // Results are a co-partitioned sibling table, not a navigation: both are keyed by month
            // so a whole month can be dropped at once.
            var results = await db.SearchResults.Where(r => r.ExecutionId == execution.Id).ToListAsync();
            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal(execution.StartedAt, r.FoundAt));

            // Each row is attributed to the indexer that returned it by id, including the dedup winner.
            var ids = await db.Indexers.ToDictionaryAsync(i => i.Name, i => i.Id);
            Assert.Equal(ids["Alpha"], results.Single(r => r.ReleaseGuid == "shared").IndexerId);
            Assert.Equal(ids["Beta"], results.Single(r => r.ReleaseGuid == "only-b").IndexerId);
        }
    }

    [Fact]
    public async Task Add_indexer_refuses_internal_or_malformed_base_urls()
    {
        await using var scope = _provider.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();

        foreach (var badUrl in new[] { "http://169.254.169.254/torznab", "http://127.0.0.1/x", "ftp://example.com", "not a url" })
        {
            var result = await admin.AddIndexerAsync("bad", IndexerProtocol.Torznab, badUrl, 1);
            Assert.True(result.IsFailure);
            Assert.Equal("discovery.invalid_base_url", result.Error.Code);
        }

        Assert.True((await admin.AddIndexerAsync("Good", IndexerProtocol.Torznab, "https://indexer.example/torznab", 1)).IsSuccess);
    }

    [Fact]
    public async Task Cataloguing_and_monitoring_a_movie_drives_a_search_to_completion()
    {
        WorkId workId;
        MonitoredTargetId targetId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
            workId = (await catalog.AddMovieAsync("Interstellar", 2014, [new ExternalId(MetadataProvider.Imdb, "tt0816692")])).Value;

            var monitoring = scope.ServiceProvider.GetRequiredService<IMonitoringCommands>();
            targetId = (await monitoring.ApplyMonitoringPolicyAsync(workId, MonitoringMode.All)).Value;

            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            Assert.True((await admin.AddIndexerAsync("Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1)).IsSuccess);
        }

        _indexers.Set("Alpha", Candidate("g-1", "Interstellar 2014 1080p BluRay", "Alpha"));

        // Monitoring emits SearchRequested → relay hands it to Discovery → Discovery enqueues
        // ExecuteSearch → worker runs the search + SearchCompleted → relay delivers it.
        await RunEvaluateMissingAsync();
        await DrainOutboxAsync();
        await DrainCommandsAsync();
        await DrainOutboxAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var execution = await db.SearchExecutions.SingleAsync(e => e.Term == "Interstellar");
            Assert.Equal(2014, execution.Year);
            Assert.Single(await db.SearchResults.Where(r => r.ExecutionId == execution.Id).ToListAsync());
        }

        Assert.Contains(_completed.Events, e => e.TargetId == targetId.Value && e.ResultCount == 1);
    }

    [Fact]
    public async Task A_series_search_request_drives_a_tvsearch_to_completion()
    {
        // Arrange — a TV-capable indexer and a monitored episode's search request. The request is
        // delivered straight to Discovery's handler: Monitoring's series materialisation is a
        // different module's increment, and Discovery must not depend on it to be exercised.
        var targetId = Uuid7.New();
        var workId = Uuid7.New();
        var episodeUnitId = Uuid7.New();
        IndexerId indexerId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var added = await admin.AddIndexerAsync("Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1);
            Assert.True(added.IsSuccess);
            indexerId = added.Value;
            Assert.True((await admin.SetCapabilitiesAsync(
                indexerId, new IndexerCapabilities(TvCategories: [5000]))).IsSuccess);
        }

        _indexers.Set("Alpha", Candidate("g-1", "The.Wire.S02E05.1080p.WEB-DL", "Alpha"));

        var criterion = new SearchCriterion(
            "The Wire", 2002, "tt0306414", null, "Episode",
            SeasonNumber: 2, EpisodeNumber: 5, TvdbId: "79126");

        // Act
        await using (var scope = _provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<SearchRequested>>();
            await handler.HandleAsync(new SearchRequested(
                targetId, workId, criterion, "Missing", "2026072810", UnitIds: [episodeUnitId]));
        }

        await DrainCommandsAsync();
        await DrainOutboxAsync();

        // Assert — the indexer was asked a real tvsearch, not a bare free-text search.
        var query = Assert.Single(_indexers.Queries);
        Assert.Contains("t=tvsearch", query.Url, StringComparison.Ordinal);
        Assert.Contains("season=2", query.Url, StringComparison.Ordinal);
        Assert.Contains("ep=5", query.Url, StringComparison.Ordinal);
        Assert.Contains("cat=5000", query.Url, StringComparison.Ordinal);

        // ...and the request context is readable back, which is how Decision learns what was asked.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var execution = await db.SearchExecutions.SingleAsync(e => e.Term == "The Wire");

            var results = scope.ServiceProvider.GetRequiredService<IReleaseSearchResults>();
            var context = await results.GetRequestContextAsync(new SearchExecutionId(execution.Id));
            Assert.NotNull(context);
            Assert.Equal("Episode", context!.ContentKind);
            Assert.Equal(2, context.SeasonNumber);
            Assert.Equal(5, context.EpisodeNumber);
            Assert.Equal("79126", context.TvdbId);
            Assert.Equal(workId, context.WorkId);
            Assert.Equal(targetId, context.TargetId);
            Assert.Equal(episodeUnitId, Assert.Single(context.RequestedUnitIds));
        }

        Assert.Contains(_completed.Events, e => e.TargetId == targetId && e.ResultCount == 1);
    }

    [Fact]
    public async Task An_unconfigured_indexer_is_still_asked_a_movie_query()
    {
        // Regression: capabilities are opt-in. An indexer nobody configured must be queried exactly
        // as it was before capabilities existed — t=movie, and no cat= at all.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            Assert.True((await admin.AddIndexerAsync("Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1)).IsSuccess);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IReleaseSearch>();
            await search.SearchAsync(new SearchCriterion("Interstellar", 2014, "tt0816692", null, "Movie"));
        }

        var query = Assert.Single(_indexers.Queries);
        Assert.Contains("t=movie", query.Url, StringComparison.Ordinal);
        Assert.Contains("imdbid=0816692", query.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("cat=", query.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minimum_seeders_filters_known_low_counts_but_keeps_unknown_counts_before_dedup()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            var added = await admin.AddIndexerAsync("Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1);
            Assert.True((await admin.SetSettingsAsync(
                added.Value, new IndexerSettings(MinimumSeeders: 5))).IsSuccess);
        }

        _indexers.Set("Alpha",
            new ReleaseCandidate("low", "Low", "magnet:?xt=urn:btih:low", ReleaseProtocol.Torrent, 1, 4, null, "Alpha"),
            new ReleaseCandidate("unknown", "Unknown", "magnet:?xt=urn:btih:unknown", ReleaseProtocol.Torrent, 1, null, null, "Alpha"));

        await using var searchScope = _provider.CreateAsyncScope();
        var outcome = await searchScope.ServiceProvider.GetRequiredService<IReleaseSearch>()
            .SearchAsync(new SearchCriterion("Sintel", null, null, null, "Movie"));

        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.Guid == "low");
        Assert.Contains(outcome.Candidates, candidate => candidate.Guid == "unknown");
    }

    [Fact]
    public async Task One_unstorable_release_does_not_sink_the_search_for_every_indexer()
    {
        // Arrange — one feed returns everything a hostile indexer can: a link too long to keep
        // intact, an overlong guid and title, and NULs (PostgreSQL refuses them in text).
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            Assert.True((await admin.AddIndexerAsync("Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1)).IsSuccess);
            Assert.True((await admin.AddIndexerAsync("Beta", IndexerProtocol.Torznab, "https://beta.example/torznab", 2)).IsSuccess);
        }

        var longGuid = new string('g', 700);
        _indexers.Set("Alpha",
            new ReleaseCandidate(
                "huge-link", "Huge link", "magnet:?xt=urn:btih:abc&dn=" + new string('x', 3000),
                ReleaseProtocol.Torrent, 1, 10, null, "Alpha"),
            new ReleaseCandidate(
                "nul-link", "NUL link", "magnet:?xt=urn:btih:abc\0", ReleaseProtocol.Torrent, 1, 10, null, "Alpha"),
            new ReleaseCandidate(
                longGuid, new string('t', 1500), "magnet:?xt=urn:btih:long", ReleaseProtocol.Torrent, 1, 10, null, "Alpha",
                TvdbId: "12\u00003", Category: new string('c', 80)),
            new ReleaseCandidate(
                "nul-title", "Sintel\0 2010", "magnet:?xt=urn:btih:nul", ReleaseProtocol.Torrent, 1, 10, null, "Alpha"));
        _indexers.Set("Beta", Candidate("from-beta", "From Beta", "Beta"));

        // Act
        SearchOutcome outcome;
        await using (var scope = _provider.CreateAsyncScope())
        {
            outcome = await scope.ServiceProvider.GetRequiredService<IReleaseSearch>()
                .SearchAsync(new SearchCriterion("Sintel", null, null, null, "Movie"));
        }

        // Assert — the links that cannot be kept intact are gone, the rest is clipped to fit, and the
        // other indexer's result survived the insert.
        Assert.Equal(
            ["from-beta", longGuid[..SearchResult.ReleaseGuidMaxLength], "nul-title"],
            outcome.Candidates.Select(c => c.Guid).Order(StringComparer.Ordinal));
        Assert.Equal("Sintel 2010", Assert.Single(outcome.Candidates, c => c.Guid == "nul-title").Title);
        var clipped = Assert.Single(outcome.Candidates, c => c.Guid.Length == SearchResult.ReleaseGuidMaxLength);
        Assert.Equal(SearchResult.TitleMaxLength, clipped.Title.Length);
        Assert.Equal("123", clipped.TvdbId);
        Assert.Equal(SearchResult.CategoryMaxLength, clipped.Category!.Length);

        // What was returned is exactly what a later read of the execution gives back.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IReleaseSearchResults>()
                .GetResultsAsync(outcome.ExecutionId);
            Assert.Equal(
                outcome.Candidates.OrderBy(c => c.Guid, StringComparer.Ordinal),
                stored.OrderBy(c => c.Guid, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Half_a_character_neither_sinks_the_search_nor_survives_into_a_link()
    {
        // Arrange — a clip that would land between the halves of an emoji, a lone surrogate a JSON
        // feed can spell as \ud800, and a link carrying one.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
            Assert.True((await admin.AddIndexerAsync("Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1)).IsSuccess);
        }

        var emojiAtTheCut = new string('t', SearchResult.TitleMaxLength - 1) + "\U0001F3AC tail";
        _indexers.Set("Alpha",
            new ReleaseCandidate("emoji", emojiAtTheCut, "magnet:?xt=urn:btih:emoji", ReleaseProtocol.Torrent, 1, 10, null, "Alpha"),
            new ReleaseCandidate("lone-\ud800", "Lone \udc00 half", "magnet:?xt=urn:btih:lone", ReleaseProtocol.Torrent, 1, 10, null, "Alpha"),
            new ReleaseCandidate("bad-link", "Bad link", "magnet:?xt=urn:btih:x&dn=\ud800", ReleaseProtocol.Torrent, 1, 10, null, "Alpha"));

        // Act
        await using var searchScope = _provider.CreateAsyncScope();
        var outcome = await searchScope.ServiceProvider.GetRequiredService<IReleaseSearch>()
            .SearchAsync(new SearchCriterion("Sintel", null, null, null, "Movie"));

        // Assert — stored, clipped before the pair rather than through it, and the link dropped.
        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Equal(new string('t', SearchResult.TitleMaxLength - 1), Assert.Single(outcome.Candidates, c => c.Guid == "emoji").Title);
        var lone = Assert.Single(outcome.Candidates, c => c.Guid == "lone-�");
        Assert.Equal("Lone � half", lone.Title);
    }

    private static ReleaseCandidate Candidate(string guid, string title, string indexerName) =>
        new(guid, title, $"magnet:?xt=urn:btih:{guid}", ReleaseProtocol.Torrent, 1_000_000L, 10, null, indexerName);

    private async Task RunEvaluateMissingAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<EvaluateMissingCommand>>();
        Assert.True((await handler.HandleAsync(new EvaluateMissingCommand())).IsSuccess);
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }

    private async Task DrainCommandsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        while (await processor.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class CompletedSink
    {
        public ConcurrentBag<(Guid SearchId, Guid? TargetId, int ResultCount)> Events { get; } = [];
    }

    private sealed class SearchCompletedSink(CompletedSink sink) : IEventHandler<SearchCompleted>
    {
        public Task HandleAsync(SearchCompleted domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Events.Add((domainEvent.SearchId, domainEvent.TargetId, domainEvent.ResultCount));
            return Task.CompletedTask;
        }
    }
}
