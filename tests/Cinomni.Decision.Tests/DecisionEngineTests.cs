using System.Collections.Concurrent;
using Cinomni.Decision.Application;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision.Tests;

/// <summary>
/// Integration tests for the decision flow against a real PostgreSQL instance: evaluating a
/// search's candidates persists explainable verdicts and selects the best; nothing acceptable
/// emits NoAcceptableRelease; the SearchCompleted event drives it end to end; and it is idempotent.
/// </summary>
public sealed class DecisionEngineTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DecisionTestHost.CreateAsync("cinomni_test_decision", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<ReleaseSelected>, ReleaseSelectedSink>();
            services.AddScoped<IEventHandler<NoAcceptableRelease>, NoAcceptableSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Evaluates_candidates_persists_reasons_and_selects_the_best()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedResultsAsync(searchId,
            ("g1", "The.Matrix.1999.720p.BluRay.x264-GRP"),
            ("g2", "The.Matrix.1999.1080p.BluRay.x264-GRP"),
            ("g3", "The.Matrix.1999.CAM.x264-GRP"));

        await EvaluateAsync(searchId, targetId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var decisionDb = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
            var evaluations = await decisionDb.ReleaseEvaluations
                .Include(e => e.Reasons)
                .Where(e => e.SearchId == searchId)
                .ToListAsync();

            Assert.Equal(3, evaluations.Count);
            Assert.Equal(2, evaluations.Count(e => e.Verdict == Verdict.Accepted));
            Assert.Equal(1, evaluations.Count(e => e.Verdict == Verdict.RejectedPermanent));
            Assert.All(evaluations, e => Assert.NotEmpty(e.Reasons));
        }

        await DrainOutboxAsync();

        // 1080p BluRay (higher rank) wins over 720p; the CAM is rejected.
        Assert.Single(_sink.Selected);
        Assert.Contains("g2", _sink.Selected);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IReleaseEvaluationQuery>();
            var forTarget = await query.GetForTargetAsync(targetId);
            Assert.Equal(3, forTarget.Count);
            Assert.All(forTarget, e => Assert.NotEmpty(e.Reasons));
        }
    }

    [Fact]
    public async Task Emits_no_acceptable_release_when_nothing_passes()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedResultsAsync(searchId, ("g1", "Movie.2020.CAM.x264-GRP"));

        await EvaluateAsync(searchId, targetId);
        await DrainOutboxAsync();

        Assert.Empty(_sink.Selected);
        Assert.Contains(searchId, _sink.NoAcceptable);
    }

    [Fact]
    public async Task The_search_completed_event_drives_evaluation_through_the_command()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedResultsAsync(searchId, ("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP"));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<SearchCompleted>>();
            await handler.HandleAsync(new SearchCompleted(searchId, targetId, 1));
        }

        await DrainCommandsAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var decisionDb = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
            Assert.Equal(1, await decisionDb.ReleaseEvaluations.CountAsync(e => e.SearchId == searchId));
        }
    }

    [Fact]
    public async Task Re_evaluating_the_same_search_is_idempotent()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        await SeedResultsAsync(searchId, ("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP"));

        await EvaluateAsync(searchId, targetId);
        await EvaluateAsync(searchId, targetId);

        await using var scope = _provider.CreateAsyncScope();
        var decisionDb = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        Assert.Equal(1, await decisionDb.ReleaseEvaluations.CountAsync(e => e.SearchId == searchId));
    }

    [Fact]
    public async Task A_long_unparseable_title_does_not_overflow_or_poison_the_batch()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();
        var longUnparseable = new string('-', 250); // no extractable title; 250 chars > the reason column
        await SeedResultsAsync(searchId,
            ("g1", "The.Matrix.1999.1080p.BluRay.x264-GRP"),
            ("g2", longUnparseable));

        await EvaluateAsync(searchId, targetId);

        await using var scope = _provider.CreateAsyncScope();
        var decisionDb = scope.ServiceProvider.GetRequiredService<DecisionDbContext>();
        var evaluations = await decisionDb.ReleaseEvaluations
            .Include(e => e.Reasons)
            .Where(e => e.SearchId == searchId)
            .ToListAsync();

        // The whole batch persisted (the long title was truncated, not overflowed).
        Assert.Equal(2, evaluations.Count);
        var rejected = Assert.Single(evaluations, e => e.Verdict == Verdict.RejectedPermanent);
        Assert.All(rejected.Reasons, r => Assert.True((r.ActualValue?.Length ?? 0) <= 200));
    }

    [Fact]
    public async Task An_empty_search_is_not_evaluated()
    {
        var searchId = Uuid7.New();
        var targetId = Uuid7.New();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<SearchCompleted>>();
            await handler.HandleAsync(new SearchCompleted(searchId, targetId, ResultCount: 0));
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            Assert.Equal(0, await operationsDb.Commands.CountAsync());
        }
    }

    private async Task SeedResultsAsync(Guid searchId, params (string Guid, string Title)[] results)
    {
        await using var scope = _provider.CreateAsyncScope();
        var discoveryDb = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var execution = new SearchExecution
        {
            Id = searchId,
            Term = "The Matrix",
            ContentKind = "Movie",
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

    private async Task DrainCommandsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        while (await processor.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class EventSink
    {
        public ConcurrentBag<string> Selected { get; } = [];

        public ConcurrentBag<Guid> NoAcceptable { get; } = [];
    }

    private sealed class ReleaseSelectedSink(EventSink sink) : IEventHandler<ReleaseSelected>
    {
        public Task HandleAsync(ReleaseSelected domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Selected.Add(domainEvent.ReleaseGuid);
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
