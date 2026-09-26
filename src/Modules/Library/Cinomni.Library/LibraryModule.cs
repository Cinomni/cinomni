using Cinomni.Catalog.Contracts;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Application;
using Cinomni.Library.Contracts;
using Cinomni.Library.EventHandlers;
using Cinomni.Library.Messaging;
using Cinomni.Library.Persistence;
using Cinomni.Operations;
using Cinomni.Subtitles.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Library;

/// <summary>
/// Registers the Library module: the owner of the <c>MediaAsset</c> — file + versions +
/// relational streams — plus the N:M asset↔target links. Reacts to Import's
/// <c>MediaAvailable</c> by persisting the asset and emitting <c>MediaAssetRegistered</c>. Requires
/// the platform kernel.
/// </summary>
public static class LibraryModule
{
    public static IServiceCollection AddLibraryModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<LibraryDbContext>();

        services.AddIntegrationEvent<MediaAssetRegistered>(LibraryEventNames.MediaAssetRegistered);

        services.AddCommand<RegisterMediaAssetCommand>(LibraryCommandNames.RegisterMediaAsset);
        services.AddCommand<AddExternalSubtitleCommand>(LibraryCommandNames.AddExternalSubtitle);
        services.AddCommand<LinkAssetUnitsCommand>(LibraryCommandNames.LinkAssetUnits);
        services.AddCommand<RelocateAssetFileCommand>(LibraryCommandNames.RelocateAssetFile);
        services.AddCommand<RemoveWorkAssetsCommand>(LibraryCommandNames.RemoveWorkAssets);

        services.AddScoped<ILibraryCommands, LibraryService>();
        services.AddScoped<ILibraryQuery, LibraryQuery>();
        // The viewer-scoped twin the endpoints bind; it consults Catalog's content access (→i).
        services.AddScoped<LibraryBrowse>();

        services.AddScoped<ICommandHandler<RegisterMediaAssetCommand>, RegisterMediaAssetCommandHandler>();
        services.AddScoped<ICommandHandler<AddExternalSubtitleCommand>, AddExternalSubtitleCommandHandler>();
        services.AddScoped<ICommandHandler<LinkAssetUnitsCommand>, LinkAssetUnitsCommandHandler>();
        services.AddScoped<ICommandHandler<RelocateAssetFileCommand>, RelocateAssetFileCommandHandler>();
        services.AddScoped<ICommandHandler<RemoveWorkAssetsCommand>, RemoveWorkAssetsCommandHandler>();

        // Consumes Import's MediaAvailable → enqueue a RegisterMediaAsset command.
        services.AddScoped<IEventHandler<MediaAvailable>, MediaAvailableHandler>();

        // Consumes Import's MediaFileRelocated → enqueue a RelocateAssetFile command.
        services.AddScoped<IEventHandler<MediaFileRelocated>, MediaFileRelocatedHandler>();
        services.AddScoped<IEventHandler<WorkRemoved>, WorkRemovedHandler>();

        // Consumes Subtitles' SubtitleAvailable → enqueue an AddExternalSubtitle command.
        services.AddScoped<IEventHandler<SubtitleAvailable>, SubtitleAvailableHandler>();

        return services;
    }

    /// <summary>Applies pending migrations for the library schema (idempotent).</summary>
    public static async Task MigrateLibraryAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
