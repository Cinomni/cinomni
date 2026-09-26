using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Persistence;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.Logging;

namespace Cinomni.Operations.Retention;

/// <summary>
/// Purges the platform kernel's own history. Three rules, each deliberately narrow:
/// <list type="bullet">
///   <item>a published outbox message older than the outbox window — never an unpublished one,
///   which is still undelivered work;</item>
///   <item>a <c>Completed</c> command older than the completed window;</item>
///   <item>a <c>Failed</c> command older than the (much longer) failed window.</item>
/// </list>
/// <c>Queued</c> and <c>Running</c> rows are untouched at any age, and so is
/// <c>operations.scheduled_job</c>, which is bounded by its registrations.
/// <para>
/// Idempotent by construction: the rules are pure predicates over time, so a redelivered command
/// deletes whatever is still eligible and reports zero when there is nothing left.
/// </para>
/// </summary>
public sealed class PurgeOperationsCommandHandler(
    OperationsDbContext dbContext,
    ILiveOptions<RetentionOptions> liveOptions,
    ILogger<PurgeOperationsCommandHandler> logger)
    : ICommandHandler<PurgeOperationsCommand>
{
    public async Task<Result> HandleAsync(
        PurgeOperationsCommand command,
        CancellationToken cancellationToken = default)
    {
        // Read at use time, never cached: a value the settings store just accepted must be honoured by
        // the very next run of this handler, not only after a restart.
        var options = liveOptions.Current;

        var now = DateTimeOffset.UtcNow;
        var outboxCutoff = now - options.OutboxRetention;
        var completedCutoff = now - options.CompletedCommandRetention;
        var failedCutoff = now - options.FailedCommandRetention;

        var outbox = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Outbox,
            m => m.Published && m.PublishedAt != null && m.PublishedAt < outboxCutoff,
            m => m.Id,
            options.BatchSize,
            cancellationToken);

        // A dead letter is kept as long as a failed command — the other thing an operator is expected to
        // look at — and then goes the same way. Without this it was never purged at all.
        var deadLetters = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Outbox,
            m => m.DeadLetteredAt != null && m.DeadLetteredAt < failedCutoff,
            m => m.Id,
            options.BatchSize,
            cancellationToken);

        var completed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Commands,
            c => c.State == CommandState.Completed && c.QueuedAt < completedCutoff,
            c => c.Id,
            options.BatchSize,
            cancellationToken);

        var failed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Commands,
            c => c.State == CommandState.Failed && c.QueuedAt < failedCutoff,
            c => c.Id,
            options.BatchSize,
            cancellationToken);

        logger.LogInformation(
            "Retention purge (operations): removed {Outbox} published and {DeadLetters} dead-lettered outbox "
            + "messages, {Completed} completed and {Failed} failed commands.",
            outbox, deadLetters, completed, failed);

        return Result.Success();
    }
}
