using Cinomni.Catalog.Contracts;
using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Tests;

/// <summary>
/// Discovery deduplicates the searches it is asked for on <c>execute-search:{targetId}:{window}</c>, and
/// <c>ux_command_idempotency_key</c> has no cleanup — a key is spent for ever. That makes the meaning of
/// <c>SearchRequested.Window</c> a correctness property: it has to identify the <em>search occasion</em>,
/// so that a genuine redelivery of one event is still collapsed while a second, distinct decision to
/// search the same target reaches an indexer.
/// <para>
/// Its own database: every <c>TestHost</c> opens with <c>EnsureDeletedAsync</c>.
/// </para>
/// </summary>
public sealed class SearchOccasionTests : IAsyncLifetime
{
    private readonly FakeIndexerCatalog _indexers = new();
    private readonly RecordingLoggerProvider _logs = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync() =>
        _provider = await DiscoveryTestHost.CreateAsync("cinomni_test_discovery_occasion", services =>
        {
            services.AddSingleton(_indexers);
            services.AddSingleton<Indexers.IIndexerClient, FakeIndexerClient>();
            services.AddLogging(logging => logging.AddProvider(_logs));
        });

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_second_search_occasion_in_the_same_clock_hour_still_reaches_an_indexer()
    {
        await CatalogueMonitoredMovieAsync();

        await SweepAsync();
        Assert.Single(_indexers.Queries);

        // The target's cooldown lapses while the wall clock stays inside the same hour. That is exactly
        // what the manual season-search endpoint arranges — it nulls LastSearchRequestedAt and re-runs
        // the sweep — and what the thirty-minute just-aired cooldown produces twice an hour by design.
        await RewindSearchStampsAsync(TimeSpan.FromHours(7));

        await SweepAsync();

        // With an hour-bucketed window both occasions built the same execute-search key, the second
        // INSERT took the ON CONFLICT DO NOTHING branch, and no indexer was ever asked again.
        Assert.Equal(2, _indexers.Queries.Count);
    }

    [Fact]
    public async Task A_redelivered_search_request_is_dropped_once_and_says_so()
    {
        var request = new SearchRequested(
            Uuid7.New(),
            Uuid7.New(),
            new SearchCriterion("The Wire", 2002, null, null, "Episode", SeasonNumber: 2, EpisodeNumber: 5),
            SearchReason.Missing.ToString(),
            SearchRequested.WindowFor(DateTimeOffset.UtcNow),
            UnitIds: [Uuid7.New()]);

        // The outbox is at-least-once, so the same event really can arrive twice — that is what the
        // idempotency key exists for, and it must still collapse to one search.
        await HandleAsync(request);
        await HandleAsync(request);

        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        var commands = await dbContext.Commands
            .Where(c => c.IdempotencyKey.StartsWith("execute-search:"))
            .CountAsync();
        Assert.Equal(1, commands);

        // ...and the drop is not silent. A command that never ran, wrote no row and failed nothing is
        // otherwise indistinguishable from a search that found nothing.
        Assert.Contains(_logs.Messages, m => m.Contains(request.Window, StringComparison.Ordinal));
    }

    private async Task CatalogueMonitoredMovieAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogCommands>();
        var workId = (await catalog.AddMovieAsync("Interstellar", 2014, [])).Value;

        var monitoring = scope.ServiceProvider.GetRequiredService<IMonitoringCommands>();
        Assert.True((await monitoring.ApplyMonitoringPolicyAsync(workId, MonitoringMode.All)).IsSuccess);

        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        Assert.True((await admin.AddIndexerAsync(
            "Alpha", IndexerProtocol.Torznab, "https://alpha.example/torznab", 1)).IsSuccess);
    }

    private async Task HandleAsync(SearchRequested request)
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IEventHandler<SearchRequested>>();
        await handler.HandleAsync(request);
    }

    /// <summary>Makes every target as due as it was <paramref name="by"/> ago, without moving the clock.</summary>
    private async Task RewindSearchStampsAsync(TimeSpan by)
    {
        await using var scope = _provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var targets = await dbContext.MonitoredTargets
            .Where(t => t.LastSearchRequestedAt != null)
            .ToListAsync();
        foreach (var target in targets)
        {
            target.LastSearchRequestedAt -= by;
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task SweepAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<EvaluateMissingCommand>>();
            Assert.True((await handler.HandleAsync(new EvaluateMissingCommand())).IsSuccess);
        }

        await DrainAsync();
    }

    /// <summary>Alternates the relay and the command worker until both are quiet.</summary>
    private async Task DrainAsync()
    {
        while (true)
        {
            int events;
            int commands;
            await using (var scope = _provider.CreateAsyncScope())
            {
                var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
                events = 0;
                int published;
                while ((published = await relay.ProcessBatchAsync()) > 0)
                {
                    events += published;
                }
            }

            await using (var scope = _provider.CreateAsyncScope())
            {
                var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
                commands = 0;
                int processed;
                while ((processed = await processor.ProcessBatchAsync()) > 0)
                {
                    commands += processed;
                }
            }

            if (events == 0 && commands == 0)
            {
                return;
            }
        }
    }
}
