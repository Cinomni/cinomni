using System.Collections.Concurrent;
using Cinomni.Catalog.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Transactions;
using Cinomni.Search.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Records the events the module publishes, so a test can assert on the announcement rather than only on
/// the row it wrote. Registered through <see cref="Register"/> on a test host.
/// </summary>
internal sealed class MonitoringEventSink
{
    public ConcurrentBag<MonitoringEnabled> Enabled { get; } = [];

    public ConcurrentBag<SearchRequested> Searches { get; } = [];

    public static Action<IServiceCollection> Register(MonitoringEventSink sink) => services =>
    {
        services.AddSingleton(sink);
        services.AddScoped<IEventHandler<MonitoringEnabled>, EnabledSink>();
        services.AddScoped<IEventHandler<SearchRequested>, SearchSink>();
    };

    private sealed class EnabledSink(MonitoringEventSink sink) : IEventHandler<MonitoringEnabled>
    {
        public Task HandleAsync(MonitoringEnabled domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Enabled.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class SearchSink(MonitoringEventSink sink) : IEventHandler<SearchRequested>
    {
        public Task HandleAsync(SearchRequested domainEvent, CancellationToken cancellationToken = default)
        {
            sink.Searches.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Drives the Monitoring slice the way the Host does — through the outbox relay and the command queue —
/// so every test exercises the real event → command spine instead of calling handlers by hand.
/// </summary>
internal sealed class MonitoringDriver(ServiceProvider host)
{
    public async Task<WorkId> AddMovieAsync(string title, int? year = null, CollectionId? collection = null)
    {
        await using var scope = host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync(title, year, [], collection);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    public async Task<WorkId> AddSeriesAsync(
        string title,
        int? year = null,
        IReadOnlyList<ExternalId>? externalIds = null,
        CollectionId? collection = null)
    {
        await using var scope = host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddSeriesAsync(title, year, externalIds ?? [], collection);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>Creates a restricted collection through Catalog's operator surface, for abuse-case tests.</summary>
    public async Task<CollectionId> CreateRestrictedCollectionAsync(string name)
    {
        await using var scope = host.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
        var created = await admin.CreateAsync(name, CollectionKind.Movies, CollectionAccessMode.Restricted);
        Assert.True(created.IsSuccess, created.Error.Message);
        return created.Value;
    }

    /// <summary>Grants a restricted collection to an account, exactly as an administrator would.</summary>
    public async Task GrantCollectionAsync(CollectionId collection, Guid userId, Guid grantedByUserId)
    {
        await using var scope = host.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICollectionAdministration>();
        Assert.True((await admin.GrantAsync(collection, userId, grantedByUserId)).IsSuccess);
    }

    /// <summary>
    /// Lands one asset covering <paramref name="unitIds"/>, exactly as Import would, and drains the spine
    /// so Monitoring's satisfaction handler has run when this returns.
    /// </summary>
    public async Task LandAssetAsync(WorkId workId, IReadOnlyList<Guid> unitIds)
    {
        await PublishAsync(new MediaAvailable(
            AssetId: Guid.NewGuid(),
            WorkId: workId.Value,
            TargetIds: [],
            ImportJobId: Guid.NewGuid(),
            DownloadTaskId: Guid.NewGuid(),
            FullPath: $"/library/{Guid.NewGuid()}.mkv",
            Size: 1024,
            MediaInfo: MediaInfo.Empty,
            UnitIds: unitIds));
        await DrainAsync();
    }

    /// <summary>Publishes a catalog structure for the series, exactly as a metadata refresh would.</summary>
    public async Task SyncStructureAsync(
        WorkId workId,
        Guid snapshotId,
        IReadOnlyList<SeasonStructureInput> seasons,
        IReadOnlyList<EpisodeStructureInput> episodes)
    {
        await using var scope = host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().SyncSeriesStructureAsync(
            workId.Value,
            new SeriesStructure(snapshotId, "tvdb", seasons, episodes));
    }

    public async Task ApplyPolicyAsync(WorkId workId, MonitoringMode mode)
    {
        await using var scope = host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
            .ApplyMonitoringPolicyAsync(workId, mode);
        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// The half of the manual season-search endpoint that touches this module: forget the season's
    /// "last searched at" stamps. The endpoint then enqueues the sweep, which the caller drives itself.
    /// </summary>
    public async Task ClearSeasonCooldownAsync(WorkId workId, int seasonNumber)
    {
        await using var scope = host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
            .ClearSearchCooldownAsync(workId, seasonNumber);
        Assert.True(result.IsSuccess);
    }

    public async Task PublishAsync(IDomainEvent domainEvent)
    {
        await using var scope = host.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        await unitOfWork.ExecuteAsync(async token => await eventBus.PublishAsync(domainEvent, token));
    }

    /// <summary>Alternates the relay and the command worker until both are quiet.</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                return;
            }
        }
    }

    /// <summary>Runs one missing sweep the way the scheduler would.</summary>
    public async Task RunEvaluateMissingAsync()
    {
        await using var scope = host.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<EvaluateMissingCommand>>();
        var result = await handler.HandleAsync(new EvaluateMissingCommand());
        Assert.True(result.IsSuccess);
    }

    public async Task<List<MonitoredTarget>> TargetsAsync(WorkId workId)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>()
            .MonitoredTargets
            .AsNoTracking()
            .Where(t => t.WorkId == workId.Value)
            .ToListAsync();
    }

    /// <summary>Rewinds every search stamp of a work so the next sweep treats its targets as due again.</summary>
    public async Task RewindSearchStampsAsync(WorkId workId, TimeSpan by)
    {
        await using var scope = host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>();
        var targets = await dbContext.MonitoredTargets.Where(t => t.WorkId == workId.Value).ToListAsync();
        foreach (var target in targets.Where(t => t.LastSearchRequestedAt is not null))
        {
            target.LastSearchRequestedAt -= by;
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Rewinds every search stamp in the database, so a whole library becomes as due as it was
    /// <paramref name="by"/> ago without the wall clock moving out of its current hour.
    /// </summary>
    public async Task RewindAllSearchStampsAsync(TimeSpan by)
    {
        await using var scope = host.CreateAsyncScope();
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

    public async Task<int> OutboxCountAsync(string eventType)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Outbox.CountAsync(m => m.EventType == eventType);
    }

    private async Task<int> DrainCommandsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    private async Task<int> DrainOutboxAsync()
    {
        await using var scope = host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }
}
