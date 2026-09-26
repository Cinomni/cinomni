using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The operator side of collections: create them, decide who may browse them, and move works between
/// them by hand, which pins them (the rules live in <see cref="CollectionRuleAdministration"/>).
/// Granting and revoking are single rows in this module's own schema — no event, no command, no outbox
/// hop, because nothing else in the system reacts to them. Revocation takes effect on the next read,
/// which is what makes it immediate for browsing and for playback alike.
/// </summary>
public sealed class CollectionAdministration(CatalogDbContext dbContext, IUnitOfWork unitOfWork)
    : ICollectionAdministration
{
    private static readonly Error NotFound = new("catalog.collection_not_found", "No such collection.");

    public async Task<IReadOnlyList<CollectionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Collections
            .AsNoTracking()
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name)
            .Select(c => new { Collection = c, WorkCount = dbContext.Works.Count(w => w.CollectionId == c.Id) })
            .ToListAsync(cancellationToken);

        return rows.Select(row => row.Collection.ToSummary(row.WorkCount)).ToList();
    }

    public async Task<Result<CollectionId>> CreateAsync(
        string name,
        CollectionKind kind,
        CollectionAccessMode accessMode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<CollectionId>.Failure(new Error("catalog.invalid_collection", "A collection name is required."));
        }

        // A new collection is asked last, so creating one never changes where an existing title sits.
        var lastPriority = await dbContext.Collections.MaxAsync(c => (int?)c.RulePriority, cancellationToken) ?? -1;
        var collection = Collection.Create(name, kind, accessMode, lastPriority + 1, DateTimeOffset.UtcNow);

        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                dbContext.Collections.Add(collection);
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            dbContext.Entry(collection).State = EntityState.Detached;
            return Result<CollectionId>.Failure(new Error(
                "catalog.collection_exists", "A collection with that name already exists."));
        }

        return Result<CollectionId>.Success(new CollectionId(collection.Id));
    }

    public async Task<Result> SetAccessModeAsync(
        CollectionId id,
        CollectionAccessMode accessMode,
        CancellationToken cancellationToken = default)
    {
        var collection = await dbContext.Collections.FirstOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        if (collection is null)
        {
            return Result.Failure(NotFound);
        }

        if (collection.AccessMode == accessMode)
        {
            return Result.Success();
        }

        collection.AccessMode = accessMode;
        await SaveAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<IReadOnlyList<Guid>> GrantedAccountsAsync(CollectionId id, CancellationToken cancellationToken = default) =>
        await dbContext.Grants
            .AsNoTracking()
            .Where(g => g.CollectionId == id.Value)
            .OrderBy(g => g.GrantedAt)
            .Select(g => g.UserId)
            .ToListAsync(cancellationToken);

    public async Task<Result> GrantAsync(
        CollectionId id,
        Guid userId,
        Guid grantedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Collections.AnyAsync(c => c.Id == id.Value, cancellationToken))
        {
            return Result.Failure(NotFound);
        }

        // The account id is not validated against Identity — Catalog does not own accounts and must not
        // read their table. A grant for an account that does not exist is inert: no session presents it.
        if (await dbContext.Grants.AnyAsync(g => g.CollectionId == id.Value && g.UserId == userId, cancellationToken))
        {
            return Result.Success();
        }

        var grant = CollectionGrant.Create(id.Value, userId, grantedByUserId, DateTimeOffset.UtcNow);
        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                dbContext.Grants.Add(grant);
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            // Granted twice at once: the composite key decided, and both callers wanted the same thing.
            dbContext.Entry(grant).State = EntityState.Detached;
        }

        return Result.Success();
    }

    public async Task<Result> RevokeAsync(CollectionId id, Guid userId, CancellationToken cancellationToken = default)
    {
        var grant = await dbContext.Grants
            .FirstOrDefaultAsync(g => g.CollectionId == id.Value && g.UserId == userId, cancellationToken);

        if (grant is null)
        {
            return Result.Success();
        }

        dbContext.Grants.Remove(grant);
        await SaveAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> MoveWorkAsync(WorkId workId, CollectionId collectionId, CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Works.AnyAsync(w => w.Id == workId.Value, cancellationToken))
        {
            return Result.Failure(new Error("catalog.work_not_found", "No such work."));
        }

        if (!await dbContext.Collections.AnyAsync(c => c.Id == collectionId.Value, cancellationToken))
        {
            return Result.Failure(NotFound);
        }

        // Visibility follows the work: whoever could see the destination can now see this title. A move
        // by hand pins it, even onto the shelf it already sits on — that is how an operator says "here,
        // whatever the rules decide later". All three columns are written whatever this context last
        // saw, so a placement committed a moment earlier cannot leave the pin on a shelf nobody chose.
        await unitOfWork.ExecuteAsync(async token => await dbContext.Works
            .Where(w => w.Id == workId.Value)
            .ExecuteUpdateAsync(set => set
                .SetProperty(w => w.CollectionId, collectionId.Value)
                .SetProperty(w => w.CollectionPinned, true)
                .SetProperty(w => w.PlacedByRuleId, (Guid?)null)
                // An operator placing it by hand is the other way a held title is released.
                .SetProperty(w => w.AwaitingMetadata, false), token), cancellationToken);
        return Result.Success();
    }

    /// <summary>A collection change emits no integration event; it is a plain write.</summary>
    private Task SaveAsync(CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);

    private static bool IsDuplicate(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
