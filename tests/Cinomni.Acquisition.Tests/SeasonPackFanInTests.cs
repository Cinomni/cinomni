using System.Collections.Concurrent;
using Cinomni.Acquisition.Contracts;
using Cinomni.Acquisition.Persistence;
using Cinomni.Decision.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Acquisition.Tests;

/// <summary>
/// Integration tests for the season-pack shape: N goals met by ONE attempt. The series root holds
/// policy and opens no goal at all; one pack selection opens exactly one attempt; and the import
/// closes every goal the pack covered — goals that never opened an attempt and, before
/// <c>MarkSatisfiedExternally</c>, had no legal way to reach <c>Available</c>.
/// </summary>
public sealed class SeasonPackFanInTests : IAsyncLifetime
{
    private const int EpisodeCount = 10;

    /// <summary>Mirrors <c>AcquisitionService.DefaultMaxAttempts</c>: what it takes to exhaust a goal.</summary>
    private const int MaxAttempts = 5;

    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await AcquisitionTestHost.CreateAsync("cinomni_test_acquisition_series", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<DownloadQueued>, DownloadQueuedSink>();
            services.AddScoped<IEventHandler<AcquisitionSucceeded>, SucceededSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_series_root_opens_no_goal()
    {
        // Arrange + Act — enabling monitoring on a series root and on one of its episodes.
        var rootTargetId = Uuid7.New();
        var episodeTargetId = Uuid7.New();
        var workId = Uuid7.New();
        var episodeUnitId = Uuid7.New();

        await EnableMonitoringAsync(rootTargetId, workId, "Series", workId);
        await EnableMonitoringAsync(episodeTargetId, workId, "Episode", episodeUnitId);
        await DrainCommandsAsync();

        // Assert — only the episode is acquirable. A goal on the root could never be satisfied by
        // any release, and a 200-episode show would carry one forever.
        var intents = await QueryAsync(q => q.ListAsync());
        var intent = Assert.Single(intents);
        Assert.Equal(episodeTargetId, intent.TargetId);
        Assert.Equal(episodeUnitId, intent.UnitId);
    }

    [Fact]
    public async Task One_pack_selection_opens_exactly_one_attempt_for_ten_episode_goals()
    {
        // Arrange — a season goal plus ten episode goals underneath it.
        var (workId, seasonTargetId, seasonUnitId, episodeUnits) = await ArrangeSeasonAsync();

        // Act — Decision picks one pack for the season target, covering every episode unit.
        var evaluationId = Uuid7.New();
        await SelectReleaseAsync(evaluationId, seasonTargetId, episodeUnits);
        await DrainCommandsAsync();
        await DrainOutboxAsync();

        // Assert — one attempt, on the season goal, claiming all ten units.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AcquisitionDbContext>();
        var attempt = await db.Attempts.Include(a => a.Units).SingleAsync();
        Assert.Equal(EpisodeCount, attempt.Units.Count);

        var seasonIntent = await QueryAsync(q => q.GetByTargetAsync(seasonTargetId));
        Assert.Equal(IntentState.Downloading, seasonIntent!.State);
        Assert.Equal(seasonUnitId, seasonIntent.UnitId);
        Assert.Equal(workId, seasonIntent.WorkId);

        // The ten episode goals are untouched: still searching, no attempt consumed.
        var episodeIntents = (await QueryAsync(q => q.ListAsync()))
            .Where(i => i.TargetId != seasonTargetId)
            .ToList();
        Assert.Equal(EpisodeCount, episodeIntents.Count);
        Assert.All(episodeIntents, i => Assert.Equal(IntentState.Searching, i.State));
        Assert.All(episodeIntents, i => Assert.Equal(0, i.AttemptCount));

        // The download hand-off carries every unit, not just the season's own.
        var queued = Assert.Single(_sink.Queued);
        Assert.Equal(episodeUnits.Order(), queued.UnitIds!.Order());
    }

    [Fact]
    public async Task Every_covered_goal_becomes_available_on_import()
    {
        // Arrange — one pack downloaded and imported for the whole season.
        var (_, seasonTargetId, _, episodeUnits) = await ArrangeSeasonAsync();
        await SelectReleaseAsync(Uuid7.New(), seasonTargetId, episodeUnits);
        await DrainCommandsAsync();

        var intentId = (await QueryAsync(q => q.GetByTargetAsync(seasonTargetId)))!.Id.Value;
        await MarkDownloadCompletedAsync(intentId);

        // Act
        var assetId = Uuid7.New();
        await CompleteImportAsync(intentId, assetId, episodeUnits);
        await DrainCommandsAsync();
        await DrainOutboxAsync();

        // Assert — every goal is met, and the ten that never tried anything still have all their
        // attempts: satisfaction from someone else's download must not cost them a retry.
        var intents = await QueryAsync(q => q.ListAsync());
        Assert.Equal(EpisodeCount + 1, intents.Count);
        Assert.All(intents, i => Assert.Equal(IntentState.Available, i.State));
        Assert.All(
            intents.Where(i => i.TargetId != seasonTargetId),
            i => Assert.Equal(0, i.AttemptCount));

        Assert.Equal(EpisodeCount + 1, _sink.Succeeded.Distinct().Count());
    }

    [Fact]
    public async Task A_redelivered_import_satisfies_each_goal_only_once()
    {
        // Arrange
        var (_, seasonTargetId, _, episodeUnits) = await ArrangeSeasonAsync();
        await SelectReleaseAsync(Uuid7.New(), seasonTargetId, episodeUnits);
        await DrainCommandsAsync();
        var intentId = (await QueryAsync(q => q.GetByTargetAsync(seasonTargetId)))!.Id.Value;
        await MarkDownloadCompletedAsync(intentId);

        var assetId = Uuid7.New();

        // Act — the outbox is at-least-once, so the same import arrives twice.
        await CompleteImportAsync(intentId, assetId, episodeUnits);
        await CompleteImportAsync(intentId, assetId, episodeUnits);
        await DrainCommandsAsync();
        await DrainOutboxAsync();

        // Assert — no duplicate transitions and no duplicate success events.
        var intents = await QueryAsync(q => q.ListAsync());
        Assert.All(intents, i => Assert.Equal(IntentState.Available, i.State));
        Assert.Equal(EpisodeCount + 1, _sink.Succeeded.Count);
    }

    [Fact]
    public async Task A_selection_for_a_unit_with_no_goal_is_skipped()
    {
        // Arrange — nothing is monitored at all.
        var targetId = Uuid7.New();

        // Act
        await SelectReleaseAsync(Uuid7.New(), targetId, [Uuid7.New(), Uuid7.New()]);
        await DrainCommandsAsync();

        // Assert — dropped without inventing a goal (and logged, which is the point of the change).
        Assert.Empty(await QueryAsync(q => q.ListAsync()));
    }

    [Fact]
    public async Task A_selection_routes_by_unit_when_the_target_has_no_goal()
    {
        // Arrange — only the episodes are monitored; Decision picked a pack for the season target,
        // which has no goal of its own. Before unit routing this selection vanished silently.
        var workId = Uuid7.New();
        var episodeUnit = Uuid7.New();
        var episodeTargetId = Uuid7.New();
        await EnableMonitoringAsync(episodeTargetId, workId, "Episode", episodeUnit);
        await DrainCommandsAsync();

        // Act
        await SelectReleaseAsync(Uuid7.New(), Uuid7.New(), [episodeUnit]);
        await DrainCommandsAsync();

        // Assert
        var intent = await QueryAsync(q => q.GetByTargetAsync(episodeTargetId));
        Assert.Equal(IntentState.Downloading, intent!.State);
    }

    [Fact]
    public async Task An_exhausted_season_goal_does_not_swallow_the_releases_its_episodes_could_use()
    {
        // Arrange — the season goal burns every attempt on packs that fail, while the ten episode
        // goals underneath it never open an attempt at all and stay perfectly healthy.
        var (_, seasonTargetId, _, episodeUnits) = await ArrangeSeasonAsync();
        var seasonIntentId = (await QueryAsync(q => q.GetByTargetAsync(seasonTargetId)))!.Id.Value;

        for (var round = 0; round < MaxAttempts; round++)
        {
            await SelectReleaseAsync(Uuid7.New(), seasonTargetId, episodeUnits);
            await DrainCommandsAsync();
            await MarkDownloadFailedAsync(seasonIntentId, "dead torrent");
        }

        Assert.Equal(
            IntentState.Exhausted,
            (await QueryAsync(q => q.GetByTargetAsync(seasonTargetId)))!.State);

        // Act — nothing changed for the sweep: the episodes are still missing, so it keeps planning
        // the pack through the season target and Decision keeps accepting releases for it.
        await SelectReleaseAsync(Uuid7.New(), seasonTargetId, episodeUnits);
        await DrainCommandsAsync();
        await DrainOutboxAsync();

        // Assert — the release was routed to a goal that could take it instead of being discarded.
        var intents = await QueryAsync(q => q.ListAsync());
        var accepting = Assert.Single(intents, i => i.State == IntentState.Downloading);
        Assert.NotEqual(seasonTargetId, accepting.TargetId);
        Assert.Contains(accepting.UnitId!.Value, episodeUnits);

        // The exhausted goal keeps its terminal state, and the hand-off still covers the whole pack.
        Assert.Equal(IntentState.Exhausted, intents.Single(i => i.TargetId == seasonTargetId).State);
        Assert.Contains(
            _sink.Queued,
            q => q.IntentId == accepting.Id.Value && q.UnitIds!.Order().SequenceEqual(episodeUnits.Order()));
    }

    [Fact]
    public async Task A_goal_with_a_download_in_flight_still_holds_its_release_back()
    {
        // The fall-through is only for a goal that has given up. A goal that is merely busy must keep
        // absorbing selections, or the same content is downloaded twice — once for the season and
        // once more for an episode inside it.
        var (_, seasonTargetId, _, episodeUnits) = await ArrangeSeasonAsync();
        await SelectReleaseAsync(Uuid7.New(), seasonTargetId, episodeUnits);
        await DrainCommandsAsync();

        // Act — a second acceptable release arrives while the pack is still downloading.
        await SelectReleaseAsync(Uuid7.New(), seasonTargetId, episodeUnits);
        await DrainCommandsAsync();

        // Assert — only the season goal ever opened an attempt.
        var intents = await QueryAsync(q => q.ListAsync());
        var busy = Assert.Single(intents, i => i.State == IntentState.Downloading);
        Assert.Equal(seasonTargetId, busy.TargetId);
        Assert.Equal(1, busy.AttemptCount);
        Assert.All(
            intents.Where(i => i.TargetId != seasonTargetId),
            i => Assert.Equal(IntentState.Searching, i.State));
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task MarkDownloadFailedAsync(Guid intentId, string reason)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
        await commands.MarkDownloadFailedAsync(intentId, reason);
    }

    private async Task<(Guid WorkId, Guid SeasonTargetId, Guid SeasonUnitId, IReadOnlyList<Guid> EpisodeUnits)>
        ArrangeSeasonAsync()
    {
        var workId = Uuid7.New();
        var seasonTargetId = Uuid7.New();
        var seasonUnitId = Uuid7.New();
        var episodeUnits = Enumerable.Range(0, EpisodeCount).Select(_ => Uuid7.New()).ToList();

        await EnableMonitoringAsync(seasonTargetId, workId, "Season", seasonUnitId);
        foreach (var unit in episodeUnits)
        {
            await EnableMonitoringAsync(Uuid7.New(), workId, "Episode", unit);
        }

        await DrainCommandsAsync();
        return (workId, seasonTargetId, seasonUnitId, episodeUnits);
    }

    private async Task EnableMonitoringAsync(Guid targetId, Guid workId, string kind, Guid unitId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<MonitoringEnabled>>();
        await handler.HandleAsync(new MonitoringEnabled(targetId, workId, "All", kind, unitId));
    }

    private async Task SelectReleaseAsync(Guid evaluationId, Guid targetId, IReadOnlyList<Guid> unitIds)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<ReleaseSelected>>();
        await handler.HandleAsync(new ReleaseSelected(evaluationId, targetId, "pack", "magnet:pack", unitIds));
    }

    private async Task MarkDownloadCompletedAsync(Guid intentId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
        await commands.MarkDownloadCompletedAsync(intentId);
    }

    private async Task CompleteImportAsync(Guid intentId, Guid assetId, IReadOnlyList<Guid> unitIds)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<ImportCompleted>>();
        await handler.HandleAsync(new ImportCompleted(Uuid7.New(), intentId, Uuid7.New(), assetId, unitIds));
    }

    private async Task<T> QueryAsync<T>(Func<IAcquisitionQuery, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>());
    }

    private async Task DrainCommandsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        while (await processor.ProcessBatchAsync() > 0)
        {
        }
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
        public ConcurrentBag<DownloadQueued> Queued { get; } = [];

        public ConcurrentBag<Guid> Succeeded { get; } = [];
    }

    private sealed class DownloadQueuedSink(EventSink sink) : IEventHandler<DownloadQueued>
    {
        public Task HandleAsync(DownloadQueued domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Queued.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class SucceededSink(EventSink sink) : IEventHandler<AcquisitionSucceeded>
    {
        public Task HandleAsync(AcquisitionSucceeded domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Succeeded.Add(domainEvent.IntentId);
            return Task.CompletedTask;
        }
    }
}
