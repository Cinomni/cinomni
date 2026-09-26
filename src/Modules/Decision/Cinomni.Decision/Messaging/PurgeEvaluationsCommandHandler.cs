using Cinomni.Decision.Application;
using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Retention;
using Cinomni.Operations.Settings;
using Microsoft.Extensions.Logging;

namespace Cinomni.Decision.Messaging;

/// <summary>
/// Removes an evaluation only when all three hold: it is older than the window, its verdict is not
/// <see cref="Verdict.Accepted"/>, and it carries no manual-override reason.
/// <para>
/// That set is exactly the evaluations the engine can never have acted on. <c>ReleaseSelected</c> is
/// published for an accepted verdict, and a hand-picked release the profile refused always carries
/// the override reason the interactive search appends. So everything that answers "why is this file
/// on disk" or "why did we take this anyway" survives indefinitely, and only sweeps that found
/// nothing worth taking are forgotten.
/// </para>
/// <para>
    /// The rule is expressible inside the <c>decision</c> schema alone — Decision may not ask Acquisition
    /// which evaluation it acted on — and the reasons follow through the schema's own cascade.
    /// A release block is a different row and is never deleted here: it is why the next sweep skips
    /// that release after this trail is gone.
/// </para>
/// </summary>
public sealed class PurgeEvaluationsCommandHandler(
    DecisionDbContext dbContext,
    ILiveOptions<DecisionRetentionOptions> options,
    RetentionOptions platformOptions,
    ILogger<PurgeEvaluationsCommandHandler> logger)
    : ICommandHandler<PurgeEvaluationsCommand>
{
    public async Task<Result> HandleAsync(
        PurgeEvaluationsCommand command,
        CancellationToken cancellationToken = default)
    {
        // Read at use time, never cached: a value the settings store just accepted must be honoured by
        // the very next run of this handler, not only after a restart.
        var cutoff = DateTimeOffset.UtcNow - options.Current.EvaluationRetention;

        var removed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.ReleaseEvaluations,
            evaluation => evaluation.CreatedAt < cutoff
                && evaluation.Verdict != Verdict.Accepted
                && !dbContext.DecisionReasons.Any(reason =>
                    reason.EvaluationId == evaluation.Id
                    && reason.Rule == InteractiveSearchService.OverrideRule),
            evaluation => evaluation.Id,
            platformOptions.BatchSize,
            cancellationToken);

        logger.LogInformation(
            "Retention purge (decision): removed {Removed} rejected evaluations; accepted verdicts and "
            + "manual overrides are retained indefinitely.",
            removed);

        return Result.Success();
    }
}
