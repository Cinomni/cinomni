using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations;
using Cinomni.Operations.Settings;
using Cinomni.Requests.Application;
using Cinomni.Requests.Contracts;
using Cinomni.Requests.EventHandlers;
using Cinomni.Requests.Messaging;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Requests;

/// <summary>
/// Registers the Requests module: the user-facing "ask for a title" flow. A request is a decision
/// waiting to happen — approving one publishes <c>MediaRequestApproved</c>, whose fulfilment command
/// catalogues the title (→i Catalog) and pulls its metadata; Monitoring then picks it up from
/// <c>WorkAdded</c> exactly as a manually added movie. Catalog's <c>WorkAvailable</c> closes the loop.
/// Requires the platform kernel (<c>AddOperations</c>) to be registered first.
/// </summary>
public static class RequestsModule
{
    public static IServiceCollection AddRequestsModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<RequestsDbContext>();

        services.AddIntegrationEvent<MediaRequested>(RequestEventNames.MediaRequested);
        services.AddIntegrationEvent<MediaRequestApproved>(RequestEventNames.MediaRequestApproved);
        services.AddIntegrationEvent<MediaRequestRejected>(RequestEventNames.MediaRequestRejected);

        services.AddCommand<FulfilMediaRequestCommand>(RequestCommandNames.FulfilMediaRequest);
        services.AddCommand<CloseFulfilledRequestsCommand>(RequestCommandNames.CloseFulfilledRequests);

        // How much of the administrator's decision queue one account may occupy. Live, so an
        // installation that finds the number wrong changes it without a restart.
        RequestQuotaOptions BindQuotas(SettingsView view) => new()
        {
            DefaultOpenRequestLimit =
                view.GetInt(RequestQuotaSettingDefinitions.DefaultOpenRequestLimit) ?? 0,
        };

        services.AddSettingDefinition(RequestQuotaSettingDefinitions.DefaultOpenRequestLimit);
        services.AddLiveOptions(BindQuotas);

        services.AddScoped<IMediaRequestCommands, MediaRequestService>();
        services.AddScoped<IMediaRequestQuery, MediaRequestQuery>();

        services.AddScoped<ICommandHandler<FulfilMediaRequestCommand>, FulfilMediaRequestCommandHandler>();
        services.AddScoped<ICommandHandler<CloseFulfilledRequestsCommand>, CloseFulfilledRequestsCommandHandler>();

        // Our own approval drives fulfilment; Catalog's availability closes the request.
        services.AddScoped<IEventHandler<MediaRequestApproved>, MediaRequestApprovedHandler>();
        services.AddScoped<IEventHandler<WorkAvailable>, WorkAvailableHandler>();

        return services;
    }

    /// <summary>Applies pending migrations for the requests schema (idempotent).</summary>
    public static async Task MigrateRequestsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RequestsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
