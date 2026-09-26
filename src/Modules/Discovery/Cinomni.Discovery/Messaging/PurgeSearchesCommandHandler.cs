using Cinomni.Discovery.Application;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Microsoft.Extensions.Logging;

namespace Cinomni.Discovery.Messaging;

/// <summary>
/// Keeps the search history bounded. Three steps, in this order: report anything sitting outside
/// every declared month, create the months the next searches will be written into (so a row never has
/// to fall into the DEFAULT partition, whose presence makes adding a range partition lock and scan),
/// then drop every month that lies entirely outside the retention window.
/// <para>
/// The diagnostic runs <b>first</b> on purpose. It exists to describe exactly the state that can make
/// the create step refuse a month, so it would be useless if that step could pre-empt it.
/// </para>
/// <para>
/// Idempotent: every step is declarative — creating an existing month is skipped, and dropping an
/// already-dropped month is a no-op — so a redelivered command changes nothing.
/// </para>
/// <para>
/// Deliberately outside <see cref="Cinomni.Operations.Transactions.IUnitOfWork"/>: this is
/// maintenance, not a business write, and DDL must not be entangled with a module's transaction.
/// </para>
/// </summary>
public sealed class PurgeSearchesCommandHandler(
    DiscoveryDbContext dbContext,
    SearchRetentionOptions options,
    ILogger<PurgeSearchesCommandHandler> logger)
    : ICommandHandler<PurgeSearchesCommand>
{
    public async Task<Result> HandleAsync(
        PurgeSearchesCommand command,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await ReportDefaultRowsAsync(cancellationToken);

        var maintenance = await SearchPartitions.EnsureAsync(dbContext, today, cancellationToken);

        if (maintenance.Blocked.Count > 0)
        {
            logger.LogWarning(
                "Search history could not attach {Blocked} monthly partitions ({Names}): rows for those months "
                + "are already in the DEFAULT partition, which PostgreSQL revalidates on attach. Recording "
                + "continues into DEFAULT; relocating those rows into their month restores O(1) drops.",
                maintenance.Blocked.Count,
                string.Join(", ", maintenance.Blocked));
        }

        // A partition covers [month, month+1). It may only be dropped once its whole range is older
        // than the cutoff, which is exactly "its month is before the cutoff's month".
        var cutoff = now - options.SearchRetention;
        var oldestMonthToKeep = DateOnly.FromDateTime(cutoff.UtcDateTime);

        var dropped = await SearchPartitions.DropExpiredAsync(dbContext, oldestMonthToKeep, cancellationToken);

        logger.LogInformation(
            "Retention purge (discovery): created {Created} monthly partitions and dropped {Dropped}.",
            maintenance.Created, dropped.Count);

        return Result.Success();
    }

    /// <summary>
    /// Names what is sitting in the DEFAULT partitions, before anything else runs. A row lands there
    /// only when no partition covered its month, so it is both the symptom of missed maintenance and
    /// the cause of a month that can no longer be attached — an operator needs the months, not a count.
    /// </summary>
    private async Task ReportDefaultRowsAsync(CancellationToken cancellationToken)
    {
        var (defaultExecutions, defaultResults) =
            await SearchPartitions.CountDefaultRowsAsync(dbContext, cancellationToken);

        if (defaultExecutions == 0 && defaultResults == 0)
        {
            return;
        }

        var months = await SearchPartitions.DefaultMonthsAsync(dbContext, cancellationToken);

        logger.LogWarning(
            "Search history has rows outside every declared month: {Executions} executions and {Results} "
            + "results sit in the DEFAULT partition for {Months}, which cannot be dropped by month. They stay "
            + "until an operator moves them into a partition for their month.",
            defaultExecutions,
            defaultResults,
            months.Count == 0 ? "an unknown month" : string.Join(", ", months));
    }
}
