using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Integration tests for the Monitoring slice against a real PostgreSQL instance: applying a
/// policy writes the target and MonitoringEnabled atomically; cataloguing a movie drives
/// monitoring end-to-end through the event → command spine; and the missing sweep requests a
/// search for monitored, still-missing targets (and only those).
/// </summary>
public sealed class MonitoringFlowTests : IAsyncLifetime
{
    private readonly EventSink _sink = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await MonitoringTestHost.CreateAsync("cinomni_test_monitoring", services =>
        {
            services.AddSingleton(_sink);
            services.AddScoped<IEventHandler<MonitoringEnabled>, MonitoringEnabledSink>();
            services.AddScoped<IEventHandler<SearchRequested>, SearchRequestedSink>();
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Applying_a_policy_writes_target_and_event_atomically_then_relay_delivers()
    {
        var workId = await AddMovieAsync("The Matrix", 1999);

        MonitoredTargetId targetId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IMonitoringCommands>();
            var result = await commands.ApplyMonitoringPolicyAsync(workId, MonitoringMode.All);
            Assert.True(result.IsSuccess);
            targetId = result.Value;
        }

        // Same commit: the target row and its MonitoringEnabled outbox message.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var monitoringDb = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();

            // Narrowed to the movie row (it used to be an unfiltered SingleAsync): a work may now hold a
            // whole target tree, but a MOVIE must still produce exactly one, and it must be its own root.
            var target = await monitoringDb.MonitoredTargets
                .Where(t => t.Kind == TargetKind.Movie)
                .SingleAsync();
            Assert.Equal(workId.Value, target.WorkId);
            Assert.Equal(workId.Value, target.TargetRef);
            Assert.Null(target.ParentTargetId);
            Assert.True(target.Monitored);
            Assert.Equal(MonitoringMode.All, target.Mode);
            Assert.True(target.IsMissing);

            Assert.Equal(1, await operationsDb.Outbox
                .CountAsync(m => m.EventType == MonitoringEventNames.MonitoringEnabled));
        }

        await DrainOutboxAsync();

        Assert.Contains(targetId.Value, _sink.Enabled);
    }

    [Fact]
    public async Task Cataloguing_a_movie_drives_monitoring_through_the_event_and_command()
    {
        var workId = await AddMovieAsync("Inception", 2010);

        // The outbox relay delivers WorkAdded to Monitoring, which enqueues ApplyMonitoringPolicy.
        await DrainOutboxAsync();
        // The command worker runs ApplyMonitoringPolicy, creating the target + MonitoringEnabled.
        await DrainCommandsAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IMonitoringQuery>();
            Assert.True(await query.IsMonitoredAsync(workId));

            var target = await query.GetByWorkAsync(workId);
            Assert.NotNull(target);
            Assert.True(target!.Monitored);
            Assert.True(target.IsMissing);
        }

        // The MonitoringEnabled produced by the command is delivered on the next relay pass.
        await DrainOutboxAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var target = await scope.ServiceProvider.GetRequiredService<IMonitoringQuery>().GetByWorkAsync(workId);
            Assert.Contains(target!.Id.Value, _sink.Enabled);
        }
    }

    [Fact]
    public async Task A_work_catalogued_unwatched_gets_a_root_that_is_switched_off()
    {
        WorkId workId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var added = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync("Suggested", 2024, [], monitored: false);
            workId = added.Value;
        }

        await DrainOutboxAsync();
        await DrainCommandsAsync();
        await DrainOutboxAsync();

        await using var read = _provider.CreateAsyncScope();
        var query = read.ServiceProvider.GetRequiredService<IMonitoringQuery>();
        var root = await query.GetByWorkAsync(workId);
        Assert.NotNull(root);
        Assert.False(root!.Monitored);
        Assert.Equal(MonitoringMode.None, root.Mode);
        Assert.DoesNotContain(root.Id.Value, _sink.Enabled);
    }

    [Fact]
    public async Task The_default_policy_arriving_late_does_not_overwrite_one_already_set()
    {
        // An operator (or an approved request) switched the work on before WorkAdded's default was
        // processed; the default only fills an empty slot.
        WorkId workId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            workId = (await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
                .AddMovieAsync("Wanted Early", 2024, [], monitored: false)).Value;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .ApplyMonitoringPolicyAsync(workId, MonitoringMode.All)).IsSuccess);
        }

        await DrainOutboxAsync();
        await DrainCommandsAsync();

        await using var read = _provider.CreateAsyncScope();
        var root = await read.ServiceProvider.GetRequiredService<IMonitoringQuery>().GetByWorkAsync(workId);
        Assert.True(root!.Monitored);
        Assert.Equal(MonitoringMode.All, root.Mode);
    }

    [Fact]
    public async Task Evaluating_missing_requests_a_search_and_respects_the_cooldown()
    {
        var workId = await AddMovieAsync("Dune", 2021);
        MonitoredTargetId targetId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .ApplyMonitoringPolicyAsync(workId, MonitoringMode.All);
            targetId = result.Value;
        }

        await RunEvaluateMissingAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            Assert.Equal(1, await operationsDb.Outbox
                .CountAsync(m => m.EventType == MonitoringEventNames.SearchRequested));

            // Narrowed the same way: one movie, one target, and the sweep stamped it.
            var target = await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>()
                .MonitoredTargets.Where(t => t.Kind == TargetKind.Movie)
                .SingleAsync();
            Assert.NotNull(target.LastSearchRequestedAt);
        }

        // A second sweep within the cooldown must not request another search.
        await RunEvaluateMissingAsync();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            Assert.Equal(1, await operationsDb.Outbox
                .CountAsync(m => m.EventType == MonitoringEventNames.SearchRequested));
        }

        await DrainOutboxAsync();
        Assert.Contains(_sink.Searches, s =>
            s.TargetId == targetId.Value && s.WorkId == workId.Value && s.Term == "Dune");
    }

    [Fact]
    public async Task An_unmonitored_target_emits_no_event_and_is_never_searched()
    {
        var workId = await AddMovieAsync("Tenet", 2020);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .ApplyMonitoringPolicyAsync(workId, MonitoringMode.None);
        }

        await RunEvaluateMissingAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            Assert.Equal(0, await operationsDb.Outbox
                .CountAsync(m => m.EventType == MonitoringEventNames.MonitoringEnabled));
            Assert.Equal(0, await operationsDb.Outbox
                .CountAsync(m => m.EventType == MonitoringEventNames.SearchRequested));

            var query = scope.ServiceProvider.GetRequiredService<IMonitoringQuery>();
            Assert.False(await query.IsMonitoredAsync(workId));
            Assert.Empty(await query.ListMissingAsync());
        }
    }

    [Fact]
    public async Task Enabling_a_target_publishes_MonitoringEnabled_and_lists_it_as_missing()
    {
        var workId = await AddMovieAsync("Arrival", 2016);
        MonitoredTargetId targetId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .ApplyMonitoringPolicyAsync(workId, MonitoringMode.None);
            targetId = result.Value;

            var enable = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .SetTargetMonitoredAsync(targetId, monitored: true);
            Assert.True(enable.IsSuccess);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            Assert.Equal(1, await operationsDb.Outbox
                .CountAsync(m => m.EventType == MonitoringEventNames.MonitoringEnabled));

            var query = scope.ServiceProvider.GetRequiredService<IMonitoringQuery>();
            Assert.True(await query.IsMonitoredAsync(workId));
            var missing = await query.ListMissingAsync();
            Assert.Contains(missing, t => t.Id == targetId);
        }
    }

    [Fact]
    public async Task Applying_a_policy_to_an_unknown_work_fails()
    {
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IMonitoringCommands>();

        var result = await commands.ApplyMonitoringPolicyAsync(WorkId.New(), MonitoringMode.All);

        Assert.True(result.IsFailure);
        Assert.Equal("monitoring.unknown_work", result.Error.Code);
    }

    [Fact]
    public async Task Applying_an_undefined_mode_is_rejected()
    {
        var workId = await AddMovieAsync("Sicario", 2015);
        await using var scope = _provider.CreateAsyncScope();
        var commands = scope.ServiceProvider.GetRequiredService<IMonitoringCommands>();

        var result = await commands.ApplyMonitoringPolicyAsync(workId, (MonitoringMode)99);

        Assert.True(result.IsFailure);
        Assert.Equal("monitoring.invalid_mode", result.Error.Code);
    }

    [Fact]
    public async Task Re_enabling_a_target_publishes_a_distinct_event_each_time()
    {
        var workId = await AddMovieAsync("Blade Runner 2049", 2017);
        MonitoredTargetId targetId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IMonitoringCommands>();
            targetId = (await commands.ApplyMonitoringPolicyAsync(workId, MonitoringMode.None)).Value;
            Assert.True((await commands.SetTargetMonitoredAsync(targetId, monitored: true)).IsSuccess);   // enable #1
            Assert.True((await commands.SetTargetMonitoredAsync(targetId, monitored: false)).IsSuccess);  // no event
            Assert.True((await commands.SetTargetMonitoredAsync(targetId, monitored: true)).IsSuccess);   // enable #2
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var operationsDb = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
            var keys = await operationsDb.Outbox
                .Where(m => m.EventType == MonitoringEventNames.MonitoringEnabled)
                .Select(m => m.IdempotencyKey)
                .ToListAsync();

            Assert.Equal(2, keys.Count);              // both enable edges emitted, the disable emitted nothing
            Assert.Equal(2, keys.Distinct().Count()); // and each enable carries a distinct idempotency key
        }
    }

    private async Task<WorkId> AddMovieAsync(string title, int? year)
    {
        await using var scope = _provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var result = await catalog.AddMovieAsync(title, year, []);
        Assert.True(result.IsSuccess);
        return result.Value;
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

    private async Task RunEvaluateMissingAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<EvaluateMissingCommand>>();
        var result = await handler.HandleAsync(new EvaluateMissingCommand());
        Assert.True(result.IsSuccess);
    }

    private sealed class EventSink
    {
        public ConcurrentBag<Guid> Enabled { get; } = [];

        public ConcurrentBag<(Guid TargetId, Guid WorkId, string Term)> Searches { get; } = [];
    }

    private sealed class MonitoringEnabledSink(EventSink sink) : IEventHandler<MonitoringEnabled>
    {
        public Task HandleAsync(MonitoringEnabled domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Enabled.Add(domainEvent.TargetId);
            return Task.CompletedTask;
        }
    }

    private sealed class SearchRequestedSink(EventSink sink) : IEventHandler<SearchRequested>
    {
        public Task HandleAsync(SearchRequested domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Searches.Add((domainEvent.TargetId, domainEvent.WorkId, domainEvent.Criterion.Term));
            return Task.CompletedTask;
        }
    }
}
