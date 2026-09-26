using System.Data.Common;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cinomni.Operations.Transactions;

/// <summary>
/// Enlists a module's <see cref="DbContext"/> in the unit-of-work transaction, so its writes
/// join the same transaction as the outbox. Every module context (other than the owner)
/// registers one of these.
/// </summary>
public interface ITransactionEnlister
{
    Task EnlistAsync(DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Detaches the shared transaction once it has completed, leaving the context free to run its own
    /// statements again. Enlisting is not self-reversing: EF keeps the context pointing at the
    /// transaction after it commits, and the next statement on that context then binds to a finished
    /// transaction.
    /// </summary>
    Task ReleaseAsync();

    /// <summary>
    /// Forgets everything the context is tracking, so writes the database has just rolled back cannot
    /// be flushed again. Only the failure path calls this: a rollback undoes the rows but leaves the
    /// entities Added/Modified on the context, and the next unit of work in the same scope would
    /// re-issue them from its own <c>SaveChangesAsync</c>.
    /// </summary>
    void DiscardChanges();
}

public sealed class DbContextEnlister<TContext>(TContext context) : ITransactionEnlister
    where TContext : DbContext
{
    public async Task EnlistAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        // No per-SaveChanges savepoints: the unit of work rolls the whole transaction back on
        // failure, and savepoints break when the transaction is shared across contexts.
        context.Database.AutoSavepointsEnabled = false;
        await context.Database.UseTransactionAsync(transaction, cancellationToken);
    }

    // Purely local bookkeeping (no I/O), and it must run even when the caller is being cancelled,
    // so it deliberately takes no cancellation token.
    public Task ReleaseAsync() => context.Database.UseTransactionAsync(null);

    public void DiscardChanges() => context.ChangeTracker.Clear();
}

/// <summary>
/// Runs work inside a single database transaction shared by every module context on the
/// scoped connection, so a module's state change and the events it publishes to the outbox
/// commit atomically or not at all.
/// </summary>
public interface IUnitOfWork
{
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
}

public sealed class UnitOfWork(OperationsDbContext owner, IEnumerable<ITransactionEnlister> enlisters) : IUnitOfWork
{
    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        // The outbox context owns the transaction (EF manages its lifecycle); module contexts
        // share the same underlying DbTransaction on the shared connection.
        owner.Database.AutoSavepointsEnabled = false;
        await using var transaction = await owner.Database.BeginTransactionAsync(cancellationToken);
        var dbTransaction = transaction.GetDbTransaction();

        foreach (var enlister in enlisters)
        {
            await enlister.EnlistAsync(dbTransaction, cancellationToken);
        }

        var committed = false;
        try
        {
            await work(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            foreach (var enlister in enlisters)
            {
                // Rolling back undoes the rows but not the change tracker: every entity this unit of
                // work added or modified is still pending on the context, so the NEXT unit of work in
                // the same scope would flush them from its own SaveChangesAsync and inherit their
                // failure. Discard them — but only on the failure path, because a caller that
                // committed legitimately keeps working with the entities it just saved.
                if (!committed)
                {
                    enlister.DiscardChanges();
                }

                // Enlisting is not symmetrical on its own. EF leaves every module context pointing at
                // this transaction after it completes, so the next statement issued by that context
                // binds to a finished transaction and fails with "Transaction is already completed".
                // That only bites when one scope runs several units of work — which is exactly what a
                // command batch is: the outbox relay routinely queues several commands from one
                // event, and the worker then runs them all in a single scope.
                await enlister.ReleaseAsync();
            }
        }
    }
}
