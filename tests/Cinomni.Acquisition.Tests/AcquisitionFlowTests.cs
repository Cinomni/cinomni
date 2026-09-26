using System.Collections.Concurrent;
using Cinomni.Acquisition.Contracts;
using Cinomni.Decision.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Acquisition.Tests;

/// <summary>
/// Integration tests for the acquisition spine against a real PostgreSQL instance: Monitoring's
/// MonitoringEnabled opens a persistent goal, Decision's ReleaseSelected feeds it a candidate and
/// hands the download off, and both reactions are idempotent. The upstream events are delivered by
/// invoking their handlers directly; the module's own events are captured through outbox sinks.
/// </summary>
public sealed class AcquisitionFlowTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await AcquisitionTestHost.CreateAsync("cinomni_test_acquisition", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<AcquisitionRequested>, RequestedSink>();
            services.AddScoped<IEventHandler<CandidateSelected>, CandidateSelectedSink>();
            services.AddScoped<IEventHandler<DownloadQueued>, DownloadQueuedSink>();
            services.AddScoped<IEventHandler<AcquisitionAttemptFailed>, AttemptFailedSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Monitoring_enabled_opens_a_searching_goal_and_announces_it()
    {
        var targetId = Uuid7.New();
        var workId = Uuid7.New();

        await EnableMonitoringAsync(targetId, workId);
        await DrainCommandsAsync();

        var intent = await QueryAsync(q => q.GetByTargetAsync(targetId));
        Assert.NotNull(intent);
        Assert.Equal(IntentState.Searching, intent!.State);
        Assert.Equal(workId, intent.WorkId);
        Assert.Equal(0, intent.AttemptCount);

        await DrainOutboxAsync();
        Assert.Contains(intent.Id.Value, _sink.Requested);
    }

    [Fact]
    public async Task Release_selected_selects_a_candidate_opens_an_attempt_and_queues_the_download()
    {
        var targetId = Uuid7.New();
        var workId = Uuid7.New();
        await EnableMonitoringAsync(targetId, workId);
        await DrainCommandsAsync();
        await DrainOutboxAsync();

        var evaluationId = Uuid7.New();
        await SelectReleaseAsync(evaluationId, targetId, "g1", "magnet:?xt=urn:btih:g1");
        await DrainCommandsAsync();

        var intent = await QueryAsync(q => q.GetByTargetAsync(targetId));
        var detail = await QueryAsync(q => q.GetAsync(intent!.Id.Value));
        Assert.NotNull(detail);
        Assert.Equal(IntentState.Downloading, detail!.Intent.State);
        var attempt = Assert.Single(detail.Attempts);
        Assert.Equal(1, attempt.Ordinal);
        Assert.Equal(AttemptState.Started, attempt.State);
        Assert.Equal("g1", attempt.ReleaseGuid);
        Assert.NotEmpty(detail.History);

        await DrainOutboxAsync();
        Assert.Contains(evaluationId, _sink.CandidateSelected);
        Assert.Contains(attempt.Id.Value, _sink.DownloadQueued);
    }

    [Fact]
    public async Task Selecting_the_same_evaluation_twice_opens_only_one_attempt()
    {
        var targetId = Uuid7.New();
        await EnableMonitoringAsync(targetId, Uuid7.New());
        await DrainCommandsAsync();

        var evaluationId = Uuid7.New();
        await SelectReleaseAsync(evaluationId, targetId, "g1", "magnet:g1");
        await SelectReleaseAsync(evaluationId, targetId, "g1", "magnet:g1");
        await DrainCommandsAsync();

        var intent = await QueryAsync(q => q.GetByTargetAsync(targetId));
        var detail = await QueryAsync(q => q.GetAsync(intent!.Id.Value));
        Assert.Single(detail!.Attempts);
    }

    [Fact]
    public async Task A_selection_with_no_goal_for_the_target_is_skipped()
    {
        var targetId = Uuid7.New(); // no MonitoringEnabled was processed for this target

        await SelectReleaseAsync(Uuid7.New(), targetId, "g1", "magnet:g1");
        await DrainCommandsAsync();

        var intents = await QueryAsync(q => q.ListAsync());
        Assert.Empty(intents);
    }

    [Fact]
    public async Task Enabling_monitoring_twice_for_a_target_opens_one_goal()
    {
        var targetId = Uuid7.New();
        var workId = Uuid7.New();

        await EnableMonitoringAsync(targetId, workId);
        await EnableMonitoringAsync(targetId, workId);
        await DrainCommandsAsync();

        var intents = await QueryAsync(q => q.ListAsync());
        Assert.Single(intents);
    }

    private async Task EnableMonitoringAsync(Guid targetId, Guid workId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<MonitoringEnabled>>();
        await handler.HandleAsync(new MonitoringEnabled(targetId, workId, "All"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_attempt_names_the_release_it_failed_on(bool failsAtImport)
    {
        // Decision ranks deterministically, so it has to be told which release failed this goal — or the
        // next sweep picks the same one, and the goal is exhausted having tried it again and again.
        var targetId = Uuid7.New();
        await EnableMonitoringAsync(targetId, Uuid7.New());
        await DrainCommandsAsync();
        await SelectReleaseAsync(Uuid7.New(), targetId, "g-dead", "magnet:?xt=urn:btih:g-dead");
        await DrainCommandsAsync();
        var intent = await QueryAsync(q => q.GetByTargetAsync(targetId));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            if (failsAtImport)
            {
                await commands.MarkDownloadCompletedAsync(intent!.Id.Value);
                await commands.MarkImportFailedAsync(intent.Id.Value, "no video file matched");
            }
            else
            {
                await commands.MarkDownloadFailedAsync(intent!.Id.Value, "dead torrent");
            }
        }

        await DrainOutboxAsync();
        var failed = Assert.Single(_sink.AttemptFailed);
        Assert.Equal(targetId, failed.TargetId);
        Assert.Equal("g-dead", failed.ReleaseGuid);
    }

    private async Task SelectReleaseAsync(Guid evaluationId, Guid targetId, string releaseGuid, string downloadUrl)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<ReleaseSelected>>();
        await handler.HandleAsync(new ReleaseSelected(evaluationId, targetId, releaseGuid, downloadUrl));
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
        public ConcurrentBag<Guid> Requested { get; } = [];

        public ConcurrentBag<Guid> CandidateSelected { get; } = [];

        public ConcurrentBag<Guid> DownloadQueued { get; } = [];

        public ConcurrentBag<AcquisitionAttemptFailed> AttemptFailed { get; } = [];
    }

    private sealed class AttemptFailedSink(EventSink sink) : IEventHandler<AcquisitionAttemptFailed>
    {
        public Task HandleAsync(AcquisitionAttemptFailed domainEvent, CancellationToken cancellationToken = default)
        {
            sink.AttemptFailed.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RequestedSink(EventSink sink) : IEventHandler<AcquisitionRequested>
    {
        public Task HandleAsync(AcquisitionRequested domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Requested.Add(domainEvent.IntentId);
            return Task.CompletedTask;
        }
    }

    private sealed class CandidateSelectedSink(EventSink sink) : IEventHandler<CandidateSelected>
    {
        public Task HandleAsync(CandidateSelected domainEvent, CancellationToken cancellationToken = default)
        {
            sink.CandidateSelected.Add(domainEvent.EvaluationId);
            return Task.CompletedTask;
        }
    }

    private sealed class DownloadQueuedSink(EventSink sink) : IEventHandler<DownloadQueued>
    {
        public Task HandleAsync(DownloadQueued domainEvent, CancellationToken cancellationToken = default)
        {
            sink.DownloadQueued.Add(domainEvent.AttemptId);
            return Task.CompletedTask;
        }
    }
}
