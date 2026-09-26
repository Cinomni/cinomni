using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Application;

/// <summary>
/// Runs <see cref="TranscodeSweep"/> on a short cadence and stops every FFmpeg process when the host
/// goes down. A hosted service rather than a queued command: what it looks after — the processes and
/// their idle clocks — lives in this process's memory, which a durable queue that could hand the work
/// to a later run has no way to reach.
/// <para>
/// The first pass runs at startup, so a previous run's leftovers are adopted or reclaimed before
/// anyone asks for them.
/// </para>
/// </summary>
public sealed class TranscodeReaper(
    IServiceScopeFactory scopeFactory,
    ActiveTranscodes transcodes,
    TimeProvider clock,
    ILogger<TranscodeReaper> logger) : BackgroundService
{
    /// <summary>
    /// Well under the idle timeout's floor, so an abandoned stream is stopped close to when it crosses
    /// the timeout rather than up to a whole extra timeout later.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await transcodes.StopAllAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TranscodeSweep>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                logger.LogWarning(failure, "The transcode sweep failed; it runs again in {Interval}.", Interval);
            }

            try
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
