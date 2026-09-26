using Cinomni.Operations.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Drives the outbox relay and the command worker by hand. Hosted services never start under a test
/// <c>ServiceProvider</c>, and driving both alternately until neither moves is what makes a multi-hop
/// reaction (event → command → event) deterministic instead of timing-dependent.
/// </summary>
internal static class MessageDriver
{
    /// <summary>Alternates commands and events until the system is quiet.</summary>
    public static async Task DrainAsync(IServiceProvider host)
    {
        while (true)
        {
            var commands = await DrainCommandsAsync(host);
            var events = await DrainOutboxAsync(host);
            if (commands == 0 && events == 0)
            {
                return;
            }
        }
    }

    private static async Task<int> DrainCommandsAsync(IServiceProvider host)
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

    private static async Task<int> DrainOutboxAsync(IServiceProvider host)
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
