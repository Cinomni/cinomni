using Cinomni.Operations.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cinomni.Host.Tests;

/// <summary>
/// Keeps the platform's background message workers out of an HTTP test host that drains the queue by
/// hand.
/// <para>
/// A <c>WebApplication</c> started with <c>StartAsync</c> runs every registered hosted service, unlike
/// a plain service provider. Left in, the outbox relay and the command worker consume the very
/// messages the test drives: the test's drain finds nothing left and returns while a message is still
/// in flight on the background worker, and the assertion that follows reads a state the flow has not
/// reached yet. It passes on a fast machine and fails on a loaded CI runner.
/// </para>
/// </summary>
internal static class OperationsWorkers
{
    /// <summary>
    /// The hosted services that consume or produce queued work: the outbox relay, the command worker
    /// and the job scheduler, which enqueues commands on its own clock.
    /// </summary>
    private static readonly Type[] MessageWorkers =
    [
        typeof(OutboxHostedService),
        typeof(CommandQueueHostedService),
        typeof(JobSchedulerHostedService),
    ];

    /// <summary>
    /// Removes the message workers so the test is the only consumer of the outbox and the command
    /// queue. Call it after the modules are registered and before the host is built.
    /// </summary>
    public static IServiceCollection WithoutMessageWorkers(this IServiceCollection services)
    {
        var workers = services
            .Where(d => d.ServiceType == typeof(IHostedService) && MessageWorkers.Contains(d.ImplementationType))
            .ToList();

        // Failing loudly if nothing matched: a renamed or re-registered worker would otherwise leave
        // the race back in place with nothing to say so.
        if (workers.Count != MessageWorkers.Length)
        {
            throw new InvalidOperationException(
                $"Expected {MessageWorkers.Length} message workers to remove, found {workers.Count}.");
        }

        foreach (var worker in workers)
        {
            services.Remove(worker);
        }

        return services;
    }
}
