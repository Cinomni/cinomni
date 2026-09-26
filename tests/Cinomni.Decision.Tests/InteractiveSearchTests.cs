using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Indexers;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision.Tests;

/// <summary>
/// The manual half of the spine, against a real PostgreSQL instance: an interactive search evaluates
/// and explains without acquiring anything, and a grab acquires exactly what a person picked — even a
/// release the profile rejected — recording that it was overridden.
/// </summary>
public sealed class InteractiveSearchTests : IAsyncLifetime
{
    private static readonly Guid TargetId = Uuid7.New();
    private static readonly Guid WorkId = Uuid7.New();

    private readonly EventSink _sink = new();
    private readonly FakeIndexer _indexer = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = await DecisionTestHost.CreateAsync("cinomni_test_decision_interactive", services =>
        {
            services.AddSingleton(_sink);
            services.AddSingleton<IIndexerClient>(_indexer);
            services.AddSingleton<ITargetSearchPlans>(new FakeSearchPlans());
            services.AddScoped<IEventHandler<ReleaseSelected>, SelectionSink>();
            services.AddScoped<IEventHandler<NoAcceptableRelease>, NoAcceptableSink>();
        });

        await SeedIndexerAsync();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_search_explains_every_candidate_and_selects_none()
    {
        _indexer.Returns(
            ("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP"),
            ("g2", "The.Matrix.1999.720p.BluRay.x264-GRP"),
            ("g3", "The.Matrix.1999.CAM.x264-GRP"));

        var result = await SearchAsync();

        Assert.Equal(3, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.NotEmpty(c.Reasons));
        // Acceptable first, best of them flagged; the CAM is still listed, because overriding it is
        // exactly what this surface is for.
        Assert.Equal("g1", result.Candidates[0].ReleaseGuid);
        Assert.True(result.Candidates[0].IsRecommended);
        Assert.Single(result.Candidates, c => c.IsRecommended);
        Assert.Contains(result.Candidates, c => c.Verdict == Verdict.RejectedPermanent);

        await DrainOutboxAsync();

        // The whole point: it decided nothing. No selection, and no "nothing was acceptable" either —
        // the automatic pipeline is not involved and must not be told anything happened.
        Assert.Empty(_sink.Selected);
        Assert.Empty(_sink.NoAcceptable);
    }

    [Fact]
    public async Task A_search_persists_the_reasons_the_explainability_endpoint_reads()
    {
        _indexer.Returns(("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP"));

        var result = await SearchAsync();

        await using var scope = _provider.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IReleaseEvaluationQuery>();
        var persisted = await query.GetForTargetAsync(TargetId);

        var evaluation = Assert.Single(persisted, e => e.Id.Value == result.Candidates[0].EvaluationId.Value);
        Assert.NotEmpty(evaluation.Reasons);
    }

    [Fact]
    public async Task Grabbing_an_accepted_release_hands_it_to_acquisition()
    {
        _indexer.Returns(("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        var result = await SearchAsync();
        var candidate = result.Candidates[0];

        var grab = await GrabAsync(candidate.EvaluationId.Value);

        Assert.True(grab.IsSuccess);
        Assert.False(grab.Value.OverrodeVerdict);

        await DrainOutboxAsync();
        var selected = Assert.Single(_sink.Selected);
        Assert.Equal("g1", selected.ReleaseGuid);
        Assert.Equal(TargetId, selected.TargetId);
    }

    [Fact]
    public async Task Grabbing_a_rejected_release_overrides_the_verdict_and_says_so_on_the_record()
    {
        _indexer.Returns(("g1", "The.Matrix.1999.CAM.x264-GRP"));
        var result = await SearchAsync();
        var candidate = Assert.Single(result.Candidates);
        Assert.NotEqual(Verdict.Accepted, candidate.Verdict);

        var grab = await GrabAsync(candidate.EvaluationId.Value);

        Assert.True(grab.IsSuccess);
        Assert.True(grab.Value.OverrodeVerdict);

        await DrainOutboxAsync();
        Assert.Single(_sink.Selected);

        // The library must always be able to answer why a file the profile rejected is on disk.
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var stored = await dbContext.ReleaseEvaluations
            .Include(e => e.Reasons)
            .FirstAsync(e => e.Id == candidate.EvaluationId.Value);

        var reason = Assert.Single(stored.Reasons, r => r.Rule == "ManualOverride");
        Assert.Equal(ReasonOutcome.Pass, reason.Outcome);
        // Appended, never rewriting what the profile decided.
        Assert.Contains(stored.Reasons, r => r.Rule != "ManualOverride" && r.Outcome == ReasonOutcome.Fail);
    }

    [Fact]
    public async Task Grabbing_the_same_evaluation_twice_selects_it_once()
    {
        _indexer.Returns(("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        var result = await SearchAsync();
        var evaluationId = result.Candidates[0].EvaluationId.Value;

        Assert.True((await GrabAsync(evaluationId)).IsSuccess);
        Assert.True((await GrabAsync(evaluationId)).IsSuccess);

        await DrainOutboxAsync();
        Assert.Single(_sink.Selected);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var stored = await dbContext.ReleaseEvaluations
            .Include(e => e.Reasons)
            .FirstAsync(e => e.Id == evaluationId);

        Assert.Single(stored.Reasons, r => r.Rule == "ManualOverride");
    }

    [Fact]
    public async Task Grabbing_an_unknown_evaluation_fails_rather_than_inventing_a_download()
    {
        var grab = await GrabAsync(Uuid7.New());

        Assert.True(grab.IsFailure);
        Assert.Equal("decision.evaluation_not_found", grab.Error.Code);
    }

    [Fact]
    public async Task Searching_a_target_the_planner_does_not_know_fails_cleanly()
    {
        await using var scope = _provider.CreateAsyncScope();
        var interactive = scope.ServiceProvider.GetRequiredService<IInteractiveSearch>();

        var result = await interactive.SearchAsync(FakeSearchPlans.UnknownTargetId);

        Assert.True(result.IsFailure);
        Assert.Equal("decision.target_not_searchable", result.Error.Code);
    }

    [Fact]
    public async Task A_search_that_finds_nothing_returns_no_candidates_and_no_events()
    {
        _indexer.Returns();

        var result = await SearchAsync();

        Assert.Empty(result.Candidates);
        await DrainOutboxAsync();
        Assert.Empty(_sink.Selected);
        Assert.Empty(_sink.NoAcceptable);
    }

    private async Task<InteractiveSearchResult> SearchAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var interactive = scope.ServiceProvider.GetRequiredService<IInteractiveSearch>();
        var result = await interactive.SearchAsync(TargetId);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Kernel.Results.Result<ManualSelection>> GrabAsync(Guid evaluationId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var interactive = scope.ServiceProvider.GetRequiredService<IInteractiveSearch>();
        return await interactive.GrabAsync(evaluationId);
    }

    private async Task SeedIndexerAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        dbContext.Indexers.Add(new Indexer
        {
            Id = Uuid7.New(),
            Name = "Fake",
            Protocol = IndexerProtocol.Torznab,
            BaseUrl = "https://indexer.example/api",
            Priority = 1,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task DrainOutboxAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        while (await relay.ProcessBatchAsync() > 0)
        {
        }
    }

    /// <summary>Stands in for the indexer fan-out: whatever the test says the endpoint returned.</summary>
    private sealed class FakeIndexer : IIndexerClient
    {
        private IReadOnlyList<ReleaseCandidate> _candidates = [];

        public void Returns(params (string Guid, string Title)[] releases) =>
            _candidates = releases
                .Select(r => new ReleaseCandidate(
                    r.Guid, r.Title, $"magnet:?xt=urn:btih:{r.Guid}", ReleaseProtocol.Torrent,
                    5_000_000_000, 10, DateTimeOffset.UtcNow, "Fake"))
                .ToList();

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
            IndexerSummary indexer,
            IndexerCredential? credential,
            string? definitionContent,
            SearchCriterion criterion,
            CancellationToken cancellationToken = default) => Task.FromResult(_candidates);
    }

    /// <summary>Stands in for Monitoring: one known target, everything else unknown.</summary>
    private sealed class FakeSearchPlans : ITargetSearchPlans
    {
        public static readonly Guid UnknownTargetId = Uuid7.New();

        public Task<TargetSearchPlan?> ResolveAsync(
            MonitoredTargetId targetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(targetId.Value == TargetId
                ? new TargetSearchPlan(
                    targetId,
                    new WorkId(WorkId),
                    TargetKind.Movie,
                    new SearchCriterion("The Matrix", 1999, null, null, "Movie"),
                    [WorkId],
                    "Movie")
                : null);
    }

    private sealed class EventSink
    {
        public ConcurrentBag<ReleaseSelected> Selected { get; } = [];

        public ConcurrentBag<Guid> NoAcceptable { get; } = [];
    }

    private sealed class SelectionSink(EventSink sink) : IEventHandler<ReleaseSelected>
    {
        public Task HandleAsync(ReleaseSelected domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Selected.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class NoAcceptableSink(EventSink sink) : IEventHandler<NoAcceptableRelease>
    {
        public Task HandleAsync(NoAcceptableRelease domainEvent, CancellationToken cancellationToken = default)
        {
            sink.NoAcceptable.Add(domainEvent.SearchId);
            return Task.CompletedTask;
        }
    }
}
