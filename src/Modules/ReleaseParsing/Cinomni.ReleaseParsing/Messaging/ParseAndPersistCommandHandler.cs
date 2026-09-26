using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.ReleaseParsing.Messaging;

/// <summary>
/// Parses a title, persists the audit record and announces <c>ReleaseParsed</c> — the write and
/// the event commit together. Idempotent: the record id is derived from the
/// source title, so a crash-retry finds the existing row and re-emits nothing. An unparseable
/// title is a normal rejection — it records nothing and reports success, so the queue never
/// retries a title that can never parse.
/// </summary>
public sealed class ParseAndPersistCommandHandler(
    IReleaseParser parser,
    ReleaseParsingDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus)
    : ICommandHandler<ParseAndPersistCommand>
{
    public async Task<Result> HandleAsync(ParseAndPersistCommand command, CancellationToken cancellationToken = default)
    {
        var result = parser.Parse(command.ReleaseTitle);
        if (result.IsFailure)
        {
            return Result.Success();
        }

        var parsed = result.Value;
        var id = DeterministicId.From(parsed.SourceTitle);

        // Already parsed and announced (its row and the ReleaseParsed committed together): no-op.
        if (await dbContext.ParsedReleases.AnyAsync(r => r.Id == id, cancellationToken))
        {
            return Result.Success();
        }

        var record = ParsedReleaseRecords.Create(parsed, id, DateTimeOffset.UtcNow);

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.ParsedReleases.Add(record);
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(
                new ReleaseParsed(id, record.CanonicalKey, parsed.ParserVersion), token);
        }, cancellationToken);

        return Result.Success();
    }
}
