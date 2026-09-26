using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Decision.Application;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Integration tests for deciding a series search against a real catalog: the wrong episode is
/// rejected with a persisted reason, a pack that closes more missing episodes wins a season goal,
/// and the selection announces exactly the catalog units it covers.
/// </summary>
public sealed class SeriesDecisionTests : IAsyncLifetime
{
    private const string SeriesTitle = "The Wire";

    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;
    private WorkId _workId;
    private IReadOnlyList<EpisodeSummary> _season2 = [];

    public async Task InitializeAsync()
    {
        _provider = await DecisionTestHost.CreateAsync("cinomni_test_decision_series", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<ReleaseSelected>, ReleaseSelectedSink>();
        });

        await using var scope = _provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        _workId = (await catalog.AddSeriesAsync(
            SeriesTitle, 2002, [new ExternalId(MetadataProvider.Tvdb, "79126")])).Value;

        await catalog.SyncSeriesStructureAsync(_workId.Value, new SeriesStructure(
            Uuid7.New(),
            "tvdb",
            [new SeasonStructureInput(2)],
            [.. Enumerable.Range(1, 6).Select(n => new EpisodeStructureInput(2, n, $"Episode {n}"))]));

        var seriesQuery = scope.ServiceProvider.GetRequiredService<ICatalogSeriesQuery>();
        _season2 = await seriesQuery.GetEpisodesAsync(_workId, 2);
        Assert.Equal(6, _season2.Count);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_wrong_episode_is_rejected_and_the_reason_is_persisted()
    {
        // Arrange — S02E05 requested; the indexer returns E05 and E06.
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedSeriesSearchAsync(searchId, "Episode", season: 2, episode: 5,
            unitIds: [UnitOf(5)],
            results:
            [
                ("right", "The.Wire.S02E05.1080p.WEB-DL.x264-GRP"),
                ("wrong", "The.Wire.S02E06.1080p.WEB-DL.x264-GRP"),
            ]);

        // Act
        await EvaluateAsync(searchId, targetId);
        await DrainOutboxAsync();

        // Assert — the wrong episode is permanently rejected with an explainable reason...
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var wrong = await db.ReleaseEvaluations.Include(e => e.Reasons)
            .SingleAsync(e => e.SearchId == searchId && e.ReleaseGuid == "wrong");
        Assert.Equal(Verdict.RejectedPermanent, wrong.Verdict);
        Assert.Contains(wrong.Reasons, r => r.Rule == "MatchesRequestedEpisode" && r.Outcome == ReasonOutcome.Fail);

        // ...and the right one is the only thing selected.
        var selected = Assert.Single(_sink.Selected);
        Assert.Equal("right", selected.ReleaseGuid);
    }

    [Fact]
    public async Task A_release_of_a_show_whose_name_starts_with_the_requested_one_is_never_selected()
    {
        // Decision is the only identity gate the pipeline has: Downloads, Import and Library all take
        // the selection on trust, so a spin-off accepted here is hardlinked as the requested episode,
        // linked to its unit and marked available — and the real episode is never searched again.
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedSeriesSearchAsync(searchId, "Episode", season: 2, episode: 5, unitIds: [UnitOf(5)],
            results: [("spinoff", "The.Wire.Tap.S02E05.1080p.WEB-DL.x264-GRP")]);

        // Act
        await EvaluateAsync(searchId, targetId);
        await DrainOutboxAsync();

        // Assert — permanently rejected, explainably, and nothing is selected at all.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var evaluation = await db.ReleaseEvaluations.Include(e => e.Reasons).SingleAsync(e => e.SearchId == searchId);
        Assert.Equal(Verdict.RejectedPermanent, evaluation.Verdict);
        var reason = Assert.Single(evaluation.Reasons, r => r.Rule == "MatchesRequestedSeries");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal(RejectionKind.Permanent, reason.Rejection);
        Assert.Equal("wire", reason.ProfileValue);
        Assert.Equal("wire tap", reason.ActualValue);
        Assert.Empty(_sink.Selected);
    }

    [Fact]
    public async Task A_release_of_a_reboot_that_shares_the_title_is_rejected_on_the_years()
    {
        // Same title, different show. The year the search carried is the work's, and the year the
        // release states is its own; when both are known and differ they are not the same series.
        var searchId = Uuid7.New();
        await SeedSeriesSearchAsync(searchId, "Episode", season: 2, episode: 5, unitIds: [UnitOf(5)],
            results: [("reboot", "The.Wire.2010.S02E05.1080p.WEB-DL.x264-GRP")], year: 2002);

        await EvaluateAsync(searchId, Uuid7.New());
        await DrainOutboxAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var evaluation = await db.ReleaseEvaluations.Include(e => e.Reasons).SingleAsync(e => e.SearchId == searchId);
        Assert.Equal(Verdict.RejectedPermanent, evaluation.Verdict);
        var reason = Assert.Single(evaluation.Reasons, r => r.Rule == "MatchesRequestedSeries");
        Assert.Equal(ReasonOutcome.Fail, reason.Outcome);
        Assert.Equal("wire (2002)", reason.ProfileValue);
        Assert.Equal("wire (2010)", reason.ActualValue);
        Assert.Empty(_sink.Selected);
    }

    [Fact]
    public async Task Selecting_a_pack_publishes_one_release_selected_with_every_covered_unit()
    {
        // Arrange — a season goal missing four episodes.
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        var missing = new[] { UnitOf(1), UnitOf(2), UnitOf(3), UnitOf(4) };
        await SeedSeriesSearchAsync(searchId, "Season", season: 2, episode: null, unitIds: missing,
            results: [("pack", "The.Wire.S02.1080p.WEB-DL.x264-GRP")]);

        // Act
        await EvaluateAsync(searchId, targetId);
        await DrainOutboxAsync();

        // Assert — one selection, carrying exactly the missing units the pack covers (not all six).
        var selected = Assert.Single(_sink.Selected);
        Assert.NotNull(selected.UnitIds);
        Assert.Equal(missing.Order(), selected.UnitIds!.Order());
    }

    [Fact]
    public async Task A_season_pack_outranks_a_single_episode_for_a_season_target()
    {
        // Arrange — the single episode has the better quality rank on purpose.
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedSeriesSearchAsync(searchId, "Season", season: 2, episode: null,
            unitIds: [UnitOf(1), UnitOf(2), UnitOf(3)],
            results:
            [
                ("single", "The.Wire.S02E01.1080p.BluRay.x264-GRP"),
                ("pack", "The.Wire.S02.1080p.WEB-DL.x264-GRP"),
            ]);

        // Act
        await EvaluateAsync(searchId, targetId);
        await DrainOutboxAsync();

        // Assert — coverage decides a season goal: one download closing three goals beats one
        // marginally better file closing one.
        var selected = Assert.Single(_sink.Selected);
        Assert.Equal("pack", selected.ReleaseGuid);
    }

    [Fact]
    public async Task A_pack_covering_more_missing_episodes_wins()
    {
        // Arrange — two packs of equal quality; only the season-2 one covers what is missing.
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedSeriesSearchAsync(searchId, "Season", season: 2, episode: null,
            unitIds: [UnitOf(1), UnitOf(2)],
            results:
            [
                ("narrow", "The.Wire.S02E01.1080p.WEB-DL.x264-GRP"),
                ("wide", "The.Wire.S02.1080p.WEB-DL.x264-GRP"),
            ]);

        // Act
        await EvaluateAsync(searchId, targetId);
        await DrainOutboxAsync();

        // Assert
        var selected = Assert.Single(_sink.Selected);
        Assert.Equal("wide", selected.ReleaseGuid);
    }

    [Fact]
    public async Task A_series_search_is_judged_by_the_series_profile()
    {
        // The silent-failure guard: if profiles were not scoped by content kind, the oldest (movie)
        // profile would judge every episode and its size bounds would reject them explainably but
        // wrongly. Here the evaluation must be attributed to the series profile.
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedSeriesSearchAsync(searchId, "Episode", season: 2, episode: 5, unitIds: [UnitOf(5)],
            results: [("g1", "The.Wire.S02E05.1080p.WEB-DL.x264-GRP")]);

        await EvaluateAsync(searchId, targetId);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var evaluation = await db.ReleaseEvaluations.SingleAsync(e => e.SearchId == searchId);
        var profile = await db.Profiles.SingleAsync(p => p.Id == evaluation.ProfileId);
        Assert.Equal(ProfileScope.Series, profile.AppliesTo);
    }

    [Fact]
    public async Task A_movie_search_is_still_judged_by_the_movie_profile()
    {
        // The movie regression, stated as a fact rather than assumed.
        var searchId = Uuid7.New();
        await SeedSeriesSearchAsync(searchId, "Movie", season: null, episode: null, unitIds: [],
            results: [("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP")], term: "The Matrix");

        await EvaluateAsync(searchId, Uuid7.New());

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var evaluation = await db.ReleaseEvaluations.Include(e => e.Reasons).SingleAsync(e => e.SearchId == searchId);
        var profile = await db.Profiles.SingleAsync(p => p.Id == evaluation.ProfileId);
        Assert.Equal(ProfileScope.Movie, profile.AppliesTo);
        Assert.DoesNotContain(evaluation.Reasons, r => r.Rule.StartsWith("MatchesRequested", StringComparison.Ordinal));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private Guid UnitOf(int episodeNumber) => _season2.Single(e => e.Number == episodeNumber).Id.Value;

    private async Task SeedSeriesSearchAsync(
        Guid searchId,
        string contentKind,
        int? season,
        int? episode,
        IReadOnlyList<Guid> unitIds,
        (string Guid, string Title)[] results,
        string? term = null,
        int? year = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var discoveryDb = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var execution = new SearchExecution
        {
            Id = searchId,
            Term = term ?? SeriesTitle,
            Year = year,
            ContentKind = contentKind,
            WorkId = _workId.Value,
            SeasonNumber = season,
            EpisodeNumber = episode,
            RequestedUnitIds = [.. unitIds],
            StartedAt = now,
            CompletedAt = now,
            ResultCount = results.Length,
        };

        foreach (var (guid, title) in results)
        {
            discoveryDb.SearchResults.Add(new SearchResult
            {
                Id = Uuid7.New(),
                ExecutionId = searchId,
                FoundAt = now,
                ReleaseGuid = guid,
                Title = title,
                DownloadUrl = $"magnet:?xt=urn:btih:{guid}",
                Protocol = ReleaseProtocol.Torrent,
                SizeBytes = 5_000_000_000,
                Seeders = 10,
                IndexerName = "Idx",
            });
        }

        discoveryDb.SearchExecutions.Add(execution);
        await discoveryDb.SaveChangesAsync();
    }

    private async Task EvaluateAsync(Guid searchId, Guid targetId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<DecisionEngine>();
        await engine.EvaluateSearchAsync(searchId, targetId);
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class EventSink
    {
        public ConcurrentBag<ReleaseSelected> Selected { get; } = [];
    }

    private sealed class ReleaseSelectedSink(EventSink sink) : IEventHandler<ReleaseSelected>
    {
        public Task HandleAsync(ReleaseSelected domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Selected.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
