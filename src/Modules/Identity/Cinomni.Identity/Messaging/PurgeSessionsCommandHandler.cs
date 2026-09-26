using Cinomni.Identity.Application;
using Cinomni.Identity.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Retention;
using Microsoft.Extensions.Logging;

namespace Cinomni.Identity.Messaging;

/// <summary>
/// Removes a session once it has been unusable for longer than the grace period — either because it
/// expired or because it was revoked. A session that can still authenticate is never touched, at any
/// age, so the sweep can never sign anybody out.
/// <para>
/// Nothing about a session is logged beyond the count: a token hash is a credential-derived value and
/// has no business in a log line.
/// </para>
/// </summary>
public sealed class PurgeSessionsCommandHandler(
    IdentityDbContext dbContext,
    SessionRetentionOptions options,
    RetentionOptions platformOptions,
    ILogger<PurgeSessionsCommandHandler> logger)
    : ICommandHandler<PurgeSessionsCommand>
{
    public async Task<Result> HandleAsync(
        PurgeSessionsCommand command,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - options.SessionGrace;

        var removed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.Sessions,
            session => session.ExpiresAt < cutoff
                || (session.RevokedAt != null && session.RevokedAt < cutoff),
            session => session.Id,
            platformOptions.BatchSize,
            cancellationToken);

        // Login challenges live for five minutes and are minted once per sign-in of an account with a
        // second factor, so they accumulate far faster than sessions do and are worthless the moment
        // they expire. The same grace period applies: nothing that could still be answered is touched.
        var staleChallenges = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.LoginChallenges,
            challenge => challenge.ExpiresAt < cutoff
                || (challenge.ConsumedAt != null && challenge.ConsumedAt < cutoff),
            challenge => challenge.Id,
            platformOptions.BatchSize,
            cancellationToken);

        logger.LogInformation(
            "Retention purge (identity): removed {Removed} dead sessions and {Challenges} spent login challenges.",
            removed,
            staleChallenges);

        return Result.Success();
    }
}
