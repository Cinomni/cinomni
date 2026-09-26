using Cinomni.Acquisition.Contracts;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Persistence;
using Cinomni.Metadata.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Messaging;
using Cinomni.Monitoring.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.SeriesSlice.Tests;

/// <summary>
/// Drives the acceptance host the way the Host itself does — through the outbox relay and the command
/// worker — and reads the resulting rows back out of every module's own schema. Hosted services never
/// start under a plain <c>ServiceProvider</c>, so both loops are turned by hand; alternating them until
/// neither moves is what makes a chain nine modules long deterministic instead of timing-dependent.
/// </summary>
internal sealed class SeriesSliceDriver(ServiceProvider host)
{
    /// <summary>The indexer every test configures; TV-capable, so a <c>t=tvsearch</c> is composed.</summary>
    public const string IndexerName = "Alpha";

    // -- driving ---------------------------------------------------------------------------------

    /// <summary>Alternates the command worker and the relay until the whole spine is quiet.</summary>
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

    public async Task<WorkId> AddSeriesAsync()
    {
        await using var scope = host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>().AddSeriesAsync(
            FakeSeriesMetadataSource.SeriesTitle,
            FakeSeriesMetadataSource.SeriesYear,
            [new ExternalId(MetadataProvider.Tvdb, FakeSeriesMetadataSource.SeriesExternalId)]);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    public async Task<WorkId> AddMovieAsync(string title, int year)
    {
        await using var scope = host.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICatalogCommands>()
            .AddMovieAsync(title, year, [new ExternalId(MetadataProvider.Tmdb, FakeSeriesMetadataSource.MovieExternalId)]);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>Refreshes a series' metadata through the real provider path (⇢ MetadataRefreshed).</summary>
    public async Task RefreshSeriesAsync(WorkId workId)
    {
        await using (var scope = host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMetadataRefresh>().RefreshAsync(
                workId.Value,
                FakeSeriesMetadataSource.ProviderName,
                FakeSeriesMetadataSource.SeriesExternalId,
                MetadataMediaKind.Series);
        }

        await DrainAsync();
    }

    public async Task ApplyPolicyAsync(WorkId workId, MonitoringMode mode)
    {
        await using (var scope = host.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMonitoringCommands>()
                .ApplyMonitoringPolicyAsync(workId, mode);
            Assert.True(result.IsSuccess);
        }

        await DrainAsync();
    }

    /// <summary>Configures the one indexer, declaring TV support so a tvsearch query is composed.</summary>
    public async Task AddTvIndexerAsync()
    {
        await using var scope = host.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIndexerAdministration>();
        var added = await admin.AddIndexerAsync(IndexerName, IndexerProtocol.Torznab, "https://alpha.example/torznab", 1);
        Assert.True(added.IsSuccess);
        Assert.True((await admin.SetCapabilitiesAsync(
            added.Value, new IndexerCapabilities(TvCategories: [5000], MovieCategories: [2000]))).IsSuccess);
    }

    /// <summary>Runs one missing sweep the scheduler would have run, then settles everything it started.</summary>
    public async Task SweepAsync()
    {
        await using (var scope = host.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<EvaluateMissingCommand>>();
            Assert.True((await handler.HandleAsync(new EvaluateMissingCommand())).IsSuccess);
        }

        await DrainAsync();
    }

    /// <summary>Feeds the status the sidecar's stream would have carried, then settles the spine.</summary>
    public async Task ApplyStatusAsync(TorrentSnapshot snapshot)
    {
        await using (var scope = host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>()
                .ApplyStatusAsync(snapshot.InfoHash, snapshot);
        }

        await DrainAsync();
    }

    /// <summary>Carries a queued download all the way to completion, exactly as the pump would.</summary>
    public async Task CompleteDownloadAsync(FakeTorrentEngine engine, string downloadUrl)
    {
        await ApplyStatusAsync(engine.SnapshotFor(downloadUrl, "downloading"));
        await ApplyStatusAsync(engine.SnapshotFor(downloadUrl, "finished", isFinished: true));
    }

    // -- reading ---------------------------------------------------------------------------------

    public async Task<List<Season>> SeasonsAsync(WorkId workId)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Seasons.AsNoTracking().Where(s => s.WorkId == workId.Value).OrderBy(s => s.Number).ToListAsync();
    }

    public async Task<List<Episode>> EpisodesAsync(WorkId workId)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Episodes.AsNoTracking().Where(e => e.WorkId == workId.Value)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number).ToListAsync();
    }

    public async Task<Work> WorkAsync(WorkId workId)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Works.AsNoTracking().SingleAsync(w => w.Id == workId.Value);
    }

    public async Task<List<MonitoredTarget>> TargetsAsync(WorkId workId)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MonitoringDbContext>()
            .MonitoredTargets.AsNoTracking().Where(t => t.WorkId == workId.Value).ToListAsync();
    }

    public async Task<IReadOnlyList<AcquisitionIntentSummary>> IntentsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>().ListAsync();
    }

    /// <summary>Every persisted evaluation with its explainable reasons — the decision audit trail.</summary>
    public async Task<List<ReleaseEvaluationRecord>> EvaluationsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DecisionDbContext>()
            .ReleaseEvaluations.AsNoTracking().Include(e => e.Reasons).ToListAsync();
    }

    public async Task<List<DownloadTask>> DownloadTasksAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DownloadsDbContext>()
            .Tasks.AsNoTracking().Include(t => t.Units).Include(t => t.Claims).ToListAsync();
    }

    public async Task<List<ImportJob>> ImportJobsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ImportDbContext>()
            .Jobs.AsNoTracking().Include(j => j.Matches).ToListAsync();
    }

    public async Task<List<MediaAsset>> AssetsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryDbContext>()
            .Assets.AsNoTracking().Include(a => a.Versions).Include(a => a.UnitLinks).Include(a => a.TargetLinks)
            .ToListAsync();
    }

    public async Task<List<AssetUnitLink>> UnitLinksAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryDbContext>()
            .UnitLinks.AsNoTracking().ToListAsync();
    }

    /// <summary>How many outbox rows of one event type were ever published (the relay never deletes).</summary>
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
