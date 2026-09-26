using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Messaging;

/// <summary>
/// Runs the <see cref="CommandProcessor"/> continuously: drains the queue, then idles
/// briefly when empty. A failing batch is logged and retried on the next tick.
/// </summary>
public sealed class CommandQueueHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CommandQueueHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var strandedAfterFailure = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Retried before every batch until it succeeds, not attempted once: the failure that stranded
            // the rows is usually the database going away, and so is the first attempt to requeue them.
            if (strandedAfterFailure)
            {
                strandedAfterFailure = !await RequeueStrandedAsync(stoppingToken);
            }

            int processed;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
                processed = await processor.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command queue batch failed; will retry.");
                strandedAfterFailure = true;
                processed = 0;
            }

            if (processed == 0)
            {
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Puts back what a failed batch left claimed. A batch that fails after its claim — the database
    /// going away mid-batch — leaves its rows Running, and only startup recovery used to requeue them.
    /// Safe to do here and now: this is the only worker, and between batches nothing it claimed is still
    /// executing, so every Running row is one that will otherwise wait for a restart.
    /// </summary>
    /// <returns>Whether the stranded rows were requeued; false means try again before the next batch.</returns>
    private async Task<bool> RequeueStrandedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CommandProcessor>().RecoverAsync(stoppingToken);
            return true;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not requeue the commands a failed batch left running; trying again.");
            return false;
        }
    }
}
