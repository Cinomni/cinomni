using System.Collections.Concurrent;
using Cinomni.Acquisition.Contracts;
using Cinomni.Decision.Application;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Decision.Tests;

/// <summary>
/// A release that failed a goal is not offered to that goal again. The engine ranks deterministically,
/// so without this the next sweep picks the same dead release, and a handful of sweeps later the goal is
/// exhausted for good having tried one release five times. The exclusion is the goal's alone: the same
/// release may be perfectly good for another.
/// </summary>
public sealed class FailedReleaseExclusionTests : IAsyncLifetime
{
    private readonly SelectedSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DecisionTestHost.CreateAsync("cinomni_test_decision_failed_exclusion", services =>
        {
            services.AddSingleton(_sink);
            // Acquisition, the producer, registers its event; this host carries Decision alone.
            services.AddIntegrationEvent<AcquisitionAttemptFailed>(AcquisitionEventNames.AcquisitionAttemptFailed);
            services.AddSingleton<ITargetSearchPlans, UnusedPlans>();
            services.AddScoped<IEventHandler<ReleaseSelected>, ReleaseSelectedHandler>();
            services.AddScoped<IEventHandler<NoAcceptableRelease>, NoAcceptableHandler>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_release_that_failed_a_goal_is_not_selected_for_it_again()
    {
        var targetId = Uuid7.New();
        await AttemptFailedAsync(targetId, "g-1080");

        var searchId = await SeedResultsAsync(
            ("g-720", "The.Matrix.1999.720p.BluRay.x264-GRP"),
            ("g-1080", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        await EvaluateAsync(searchId, targetId);

        Assert.Equal(["g-720"], _sink.Selected);

        // The explanation says why the better release was passed over.
        await using var scope = _provider.CreateAsyncScope();
        var evaluation = await scope.ServiceProvider.GetRequiredService<DecisionDbContext>().ReleaseEvaluations
            .Include(e => e.Reasons)
            .SingleAsync(e => e.SearchId == searchId && e.ReleaseGuid == "g-1080");
        Assert.Contains(evaluation.Reasons, reason => reason.Rule == DecisionEngine.FailedForTargetRule);
    }

    [Fact]
    public async Task The_exclusion_belongs_to_the_goal_that_failed_and_not_to_every_goal()
    {
        await AttemptFailedAsync(Uuid7.New(), "g-1080");

        var searchId = await SeedResultsAsync(
            ("g-720", "The.Matrix.1999.720p.BluRay.x264-GRP"),
            ("g-1080", "The.Matrix.1999.1080p.BluRay.x264-GRP"));
        await EvaluateAsync(searchId, Uuid7.New());

        Assert.Equal(["g-1080"], _sink.Selected);
    }

    [Fact]
    public async Task A_failure_delivered_twice_records_one_exclusion()
    {
        var targetId = Uuid7.New();
        var attemptId = Uuid7.New();
        await AttemptFailedAsync(targetId, "g-1080", attemptId);
        await AttemptFailedAsync(targetId, "g-1080", attemptId);
        await AttemptFailedAsync(targetId, "g-1080"); // a later attempt that failed on the same release

        await using var scope = _provider.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<DecisionDbContext>()
            .ReleaseExclusions.CountAsync(x => x.TargetId == targetId));
    }

    private async Task AttemptFailedAsync(Guid targetId, string releaseGuid, Guid? attemptId = null)
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(async token =>
                await eventBus.PublishAsync(
                    new AcquisitionAttemptFailed(Uuid7.New(), targetId, attemptId ?? Uuid7.New(), releaseGuid, "dead torrent"),
                    token));
        }

        await DrainAsync();
    }

    private async Task<Guid> SeedResultsAsync(params (string Guid, string Title)[] results)
    {
        var searchId = Uuid7.New();
        await using var scope = _provider.CreateAsyncScope();
        var discoveryDb = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var now = DateTimeOffset.UtcNow;
        discoveryDb.SearchExecutions.Add(new SearchExecution
        {
            Id = searchId,
            Term = "The Matrix",
            ContentKind = "Movie",
            StartedAt = now,
            CompletedAt = now,
            ResultCount = results.Length,
        });
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

        await discoveryDb.SaveChangesAsync();
        return searchId;
    }

    private async Task EvaluateAsync(Guid searchId, Guid targetId)
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DecisionEngine>().EvaluateSearchAsync(searchId, targetId);
        }

        await DrainAsync();
    }

    private async Task DrainAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        while (await relay.ProcessBatchAsync() + await processor.ProcessBatchAsync() > 0)
        {
        }
    }

    private sealed class UnusedPlans : ITargetSearchPlans
    {
        public Task<TargetSearchPlan?> ResolveAsync(MonitoredTargetId targetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TargetSearchPlan?>(null);
    }

    private sealed class SelectedSink
    {
        public ConcurrentQueue<string> Selected { get; } = [];
    }

    private sealed class ReleaseSelectedHandler(SelectedSink sink) : IEventHandler<ReleaseSelected>
    {
        public Task HandleAsync(ReleaseSelected domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Selected.Enqueue(domainEvent.ReleaseGuid);
            return Task.CompletedTask;
        }
    }

    private sealed class NoAcceptableHandler : IEventHandler<NoAcceptableRelease>
    {
        public Task HandleAsync(NoAcceptableRelease domainEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
