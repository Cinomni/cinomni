using Cinomni.Catalog.Application;
using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.EventHandlers;
using Cinomni.Catalog.Messaging;
using Cinomni.Catalog.Persistence;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cinomni.Catalog;

/// <summary>
/// Registers the Catalog module: the Work aggregate and external identifiers.
/// Requires the platform kernel (<c>AddOperations</c>) first — it shares its connection and
/// unit of work for atomic events.
/// </summary>
public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<CatalogDbContext>();

        TrendingListOptions BindTrending(SettingsView view) => new()
        {
            Enabled = view.GetBool(TrendingListSettingDefinitions.Enabled) ?? false,
        };
        services.AddSettingsCheck(
            [TrendingListSettingDefinitions.Enabled], BindTrending, TrendingListOptions.Check);
        services.AddLiveOptions(BindTrending);

        // Empty until Metadata's adapters replace them. A composition without those adapters adds nothing.
        services.TryAddScoped<IMetadataLists, NoTrendingLists>();
        services.TryAddScoped<IMetadataRefresh, NoMetadataRefresh>();

        services.AddIntegrationEvent<WorkAdded>(CatalogEventNames.WorkAdded);
        services.AddIntegrationEvent<WorkRemoved>(CatalogEventNames.WorkRemoved);
        services.AddIntegrationEvent<WorkAvailable>(CatalogEventNames.WorkAvailable);
        // Publishing an event that was never registered fails at runtime, not at build time.
        services.AddIntegrationEvent<SeriesStructureChanged>(CatalogEventNames.SeriesStructureChanged);
        services.AddIntegrationEvent<EpisodeAvailable>(CatalogEventNames.EpisodeAvailable);

        services.AddCommand<MarkWorkAvailableCommand>(CatalogCommandNames.MarkWorkAvailable);
        services.AddCommand<MarkEpisodeAvailableCommand>(CatalogCommandNames.MarkEpisodeAvailable);
        services.AddCommand<AttachMetadataSnapshotCommand>(CatalogCommandNames.AttachMetadataSnapshot);
        services.AddCommand<SyncSeriesStructureCommand>(CatalogCommandNames.SyncSeriesStructure);
        services.AddCommand<UpdateWorkArtworkCommand>(CatalogCommandNames.UpdateWorkArtwork);
        services.AddCommand<RefreshTrendingListCommand>(CatalogCommandNames.RefreshTrendingList);

        services.AddScoped<SeriesStructureService>();
        services.AddScoped<ICatalogCommands, CatalogCommands>();
        services.AddScoped<ICatalogQuery, CatalogQuery>();
        services.AddScoped<ICatalogSeriesQuery, CatalogSeriesQuery>();

        // Content access: the one authority every module consults before serving a work to a person.
        // Registered here and nowhere else — a Host that forgets to compose Catalog must fail to resolve
        // it and crash at startup, rather than quietly serving everything to everyone.
        // Until Metadata replaces this, a ceiling has no region to be compared in and filters nothing.
        services.TryAddSingleton<IContentRatingRegion, UnsetContentRatingRegion>();
        services.AddScoped<ContentAccess>();
        services.AddScoped<IContentAccess>(sp => sp.GetRequiredService<ContentAccess>());
        services.AddScoped<ICatalogBrowse, CatalogBrowse>();
        services.AddScoped<ICollectionAdministration, CollectionAdministration>();

        services.AddScoped<ICommandHandler<MarkWorkAvailableCommand>, MarkWorkAvailableCommandHandler>();
        services.AddScoped<ICommandHandler<MarkEpisodeAvailableCommand>, MarkEpisodeAvailableCommandHandler>();
        services.AddScoped<ICommandHandler<AttachMetadataSnapshotCommand>, AttachMetadataSnapshotCommandHandler>();
        services.AddScoped<ICommandHandler<SyncSeriesStructureCommand>, SyncSeriesStructureCommandHandler>();
        services.AddScoped<ICommandHandler<UpdateWorkArtworkCommand>, UpdateWorkArtworkCommandHandler>();
        services.AddScoped<ICommandHandler<RefreshTrendingListCommand>, RefreshTrendingListCommandHandler>();
        services.AddScoped<TrendingListRefresh>();
        services.AddScheduledJob<RefreshTrendingListCommand>(
            "catalog.refresh-trending-list", CatalogCommandNames.RefreshTrendingList, TimeSpan.FromHours(6));

        // Consumes Import's MediaAvailable → enqueue MarkWorkAvailable (movie) or one
        // MarkEpisodeAvailable per landed unit (series).
        services.AddScoped<IEventHandler<MediaAvailable>, MediaAvailableHandler>();

        // Consumes Metadata's MetadataRefreshed → enqueue AttachMetadataSnapshot (the ACL) and, for a
        // series, SyncSeriesStructure.
        services.AddScoped<IEventHandler<MetadataRefreshed>, MetadataRefreshedHandler>();

        // Consumes Metadata's MetadataArtworkSelected → enqueue an UpdateWorkArtwork command.
        services.AddScoped<IEventHandler<MetadataArtworkSelected>, MetadataArtworkSelectedHandler>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations for the catalog schema and makes sure the default collection exists
    /// (idempotent). The migration seeds it for an upgrade; this is what covers a database created some
    /// other way, since a work with nowhere to sit cannot be added.
    /// </summary>
    public static async Task MigrateCatalogAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        if (!await dbContext.Collections.AnyAsync(cancellationToken))
        {
            dbContext.Collections.Add(DefaultCollection.Create(DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
