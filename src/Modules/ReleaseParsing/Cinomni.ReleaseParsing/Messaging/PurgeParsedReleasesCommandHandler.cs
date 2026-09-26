using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Retention;
using Cinomni.ReleaseParsing.Application;
using Cinomni.ReleaseParsing.Persistence;
using Microsoft.Extensions.Logging;

namespace Cinomni.ReleaseParsing.Messaging;

/// <summary>
/// Ages out parse records older than the window. Self-healing rather than lossy: the record's id is
/// derived from the source title and <see cref="ParseAndPersistCommand"/> is a get-or-create, so a
/// title seen again after its row was purged is simply parsed and persisted afresh.
/// <para>The rule-set versions are never touched: they are the audit boundary, one row per version.</para>
/// </summary>
public sealed class PurgeParsedReleasesCommandHandler(
    ReleaseParsingDbContext dbContext,
    ReleaseParsingRetentionOptions options,
    RetentionOptions platformOptions,
    ILogger<PurgeParsedReleasesCommandHandler> logger)
    : ICommandHandler<PurgeParsedReleasesCommand>
{
    public async Task<Result> HandleAsync(
        PurgeParsedReleasesCommand command,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - options.ParseRetention;

        var removed = await RetentionPurge.DeleteInBatchesAsync(
            dbContext.ParsedReleases,
            parsed => parsed.CreatedAt < cutoff,
            parsed => parsed.Id,
            platformOptions.BatchSize,
            cancellationToken);

        logger.LogInformation("Retention purge (parsing): removed {Removed} parse records.", removed);

        return Result.Success();
    }
}
