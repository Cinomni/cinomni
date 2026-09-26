using Cinomni.Downloads.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.RealTime.Streaming;

/// <summary>
/// Pushes the active downloads to whoever is watching. Transfer figures change continuously and raise
/// no integration event — they are not decisions, and putting a percentage through the outbox would be
/// absurd — so this is the one signal the server generates on a clock rather than in reaction to
/// something.
/// <para>
/// It is still the opposite of the polling it replaces. It reads once for every watcher instead of once
/// per watcher, it reads nothing at all while nobody is connected or nothing is transferring, and the
/// browser makes no request whatsoever: the snapshot arrives on the stream it already has open.
/// </para>
/// </summary>
public sealed class DownloadProgressPublisher(
    RealtimeHub hub,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadProgressPublisher> logger) : BackgroundService
{
    /// <summary>Snapshot cadence. Fast enough to read as live, slow enough to be one query.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>Whether the previous tick had anything to report, so the emptying edge is sent once.</summary>
    private bool _hadActive;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await PublishSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed read is a missed frame, never a stopped pump: the next tick tries again.
                logger.LogWarning(ex, "Could not publish a download progress snapshot.");
            }
        }
    }

    private async Task PublishSnapshotAsync(CancellationToken cancellationToken)
    {
        // Nobody with the audience for it is listening: do not touch the database at all.
        if (!hub.HasOperators)
        {
            _hadActive = false;
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<IDownloadQuery>();
        var tasks = await query.ListActiveAsync(cancellationToken);

        // Nothing transferring: say so exactly once, so a client whose last frame showed a download
        // sees it clear, and then go quiet.
        if (tasks.Count == 0 && !_hadActive)
        {
            return;
        }

        _hadActive = tasks.Count > 0;
        hub.Publish(new RealtimeMessage(
            RealtimeMessage.Topics.DownloadProgress,
            RealtimeAudience.Operators,
            tasks.Select(DownloadTaskWire.From).ToList()));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
