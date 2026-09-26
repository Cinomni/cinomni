using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Messaging;

/// <summary>
/// Runs the <see cref="OutboxRelay"/> continuously: drains pending messages, then idles
/// for a short interval when the outbox is empty. A message whose handler fails is retried with a
/// backoff and eventually dead-lettered by the relay itself, so it never holds back the rest; a batch
/// that fails as a whole (the database, not a handler) is logged and retried on the next tick.
/// </summary>
public sealed class OutboxHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
                published = await relay.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay batch failed; will retry.");
                published = 0;
            }

            if (published == 0)
            {
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
