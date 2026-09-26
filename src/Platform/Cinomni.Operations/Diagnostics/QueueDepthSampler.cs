using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Diagnostics;

/// <summary>
/// Samples the spine's backlog on a fixed cadence and hands it to <see cref="OperationsMetrics"/>.
/// <para>
/// A backlog is a level, not an event, so it can only be published as a gauge — and a gauge callback
/// runs whenever the exporter decides to collect. Querying the database from that callback would put
/// unbounded, unpredictable load behind a scrape interval configured somewhere this process cannot see,
/// on the same connection pool the acquisition spine is using. So the reading is taken here, on a
/// cadence this process owns, and the callback only ever reads the last sample.
/// </para>
/// <para>
/// Sampling is read-only and takes no transaction. A failed pass is logged and the previous sample is
/// left in place: a stale depth is a far better answer than a worker that stops, and telemetry must
/// never be able to take the installation down.
/// </para>
/// </summary>
public sealed class QueueDepthSampler(OperationsDbContext dbContext)
{
    /// <summary>Reads one sample and publishes it. Callable directly so a test needs no timer.</summary>
    public async Task<QueueDepthSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // The unpublished half is exactly what ix_outbox_unpublished covers, so it stays cheap however
        // large the table grows. Dead letters are not pending: they will never be delivered, and
        // counting them would make "the outbox is stuck" permanently true after the first one (they have
        // their own counter, cinomni.outbox.dead_lettered). The command half is a grouped count over the whole table: it rides
        // ix_command_purge on (State, QueuedAt) as an index-only scan, but it still touches every row a
        // retention window keeps — thirty days of completed commands and a hundred and eighty of failed
        // ones. That is a known, bounded cost paid on this sampler's own cadence rather than on an
        // exporter's, and it is the reason the cadence is slow.
        var pending = await dbContext.Outbox
            .AsNoTracking()
            .Where(message => !message.Published && message.DeadLetteredAt == null)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Oldest = group.Min(message => (DateTimeOffset?)message.OccurredAt),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var commandsByState = await dbContext.Commands
            .AsNoTracking()
            .GroupBy(command => command.State)
            .Select(group => new { State = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken);

        // Every state is published, including the ones with no rows: a series that disappears when it
        // reaches zero reads as "no data" on a dashboard, which is the opposite of what it means.
        var depths = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [CommandState.Queued] = 0,
            [CommandState.Running] = 0,
            [CommandState.Completed] = 0,
            [CommandState.Failed] = 0,
        };

        foreach (var entry in commandsByState)
        {
            depths[entry.State] = entry.Count;
        }

        var oldestAge = pending?.Oldest is { } oldest ? now - oldest : TimeSpan.Zero;
        var snapshot = new QueueDepthSnapshot(pending?.Count ?? 0, oldestAge, depths);

        OperationsMetrics.PublishQueueDepths(snapshot);
        return snapshot;
    }
}

/// <summary>Drives <see cref="QueueDepthSampler"/> on a fixed tick.</summary>
public sealed class QueueDepthSamplerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<QueueDepthSamplerHostedService> logger)
    : BackgroundService
{
    /// <summary>
    /// Slower than any exporter's scrape on purpose. A backlog that matters is a backlog that persists
    /// for minutes; sampling faster would only add database load to make a number jitter.
    /// </summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sampler = scope.ServiceProvider.GetRequiredService<QueueDepthSampler>();
                await sampler.SampleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Telemetry must never take the installation down. The previous sample stays published.
                logger.LogWarning(exception, "Queue-depth sampling failed; the previous sample stands.");
            }

            await Task.Delay(SampleInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
