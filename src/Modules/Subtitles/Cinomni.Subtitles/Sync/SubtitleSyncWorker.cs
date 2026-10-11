using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Subtitles.Sync;

/// <summary>
/// Runs <see cref="SubtitleSyncProcessor"/> in the background, owned by this module, with its own
/// <see cref="SubtitleSyncOptions.Concurrency"/> lanes (one by default).
/// <para>
/// Not on the shared command worker on purpose: that worker runs every module's commands one after
/// another, and a pass decodes minutes of audio, so a season pack's subtitles would hold imports,
/// downloads and notifications up behind them. The pending rows live in this module's schema, so the
/// work survives a restart exactly as a queued command would.
/// </para>
/// <para>
/// Startup releases the claims a previous run left, then every lane drains the queue and idles when it
/// is empty. The first lane also runs the backstop sweep every few minutes. A test host does not start
/// hosted services and drives the processor itself.
/// </para>
/// </summary>
public sealed class SubtitleSyncWorker(
    IServiceScopeFactory scopeFactory,
    SubtitleSyncOptions options,
    ILogger<SubtitleSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await ReleaseClaimsAsync(stoppingToken))
        {
            return;
        }

        var lanes = Enumerable.Range(0, options.Concurrency)
            .Select(lane => RunLaneAsync(sweeps: lane == 0, stoppingToken));
        await Task.WhenAll(lanes);
    }

    /// <summary>Retried until it succeeds: the database may not be up yet when the host starts.</summary>
    private async Task<bool> ReleaseClaimsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SubtitleSyncProcessor>().ReleaseClaimsAsync(stoppingToken);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not release the subtitle sync claims left by the last run; trying again.");
                if (!await DelayAsync(IdleDelay, stoppingToken))
                {
                    return false;
                }
            }
        }

        return false;
    }

    private async Task RunLaneAsync(bool sweeps, CancellationToken stoppingToken)
    {
        var nextSweep = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<SubtitleSyncProcessor>();
                if (sweeps && DateTimeOffset.UtcNow >= nextSweep)
                {
                    nextSweep = DateTimeOffset.UtcNow + SweepInterval;
                    await processor.SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
                }

                processed = await processor.ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Subtitle synchronization failed; will retry.");
            }

            if (!processed && !await DelayAsync(IdleDelay, stoppingToken))
            {
                return;
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
