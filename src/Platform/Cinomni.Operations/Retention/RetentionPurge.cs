using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Cinomni.Operations.Retention;

/// <summary>
/// The one way a module ages rows out of its own schema: a bounded, resumable, set-based delete.
/// <para>
/// Deliberately <b>outside</b> <c>IUnitOfWork</c>. A purge is nobody's business transaction; running
/// it inside the shared unit of work would hold the scoped transaction for the whole sweep and block
/// every module write in that scope. Each batch commits on its own, so an interrupted purge simply
/// resumes on the next run — there is no partial state to recover.
/// </para>
/// <para>
/// Children configured with <c>OnDelete(DeleteBehavior.Cascade)</c> follow through the database's own
/// <c>ON DELETE CASCADE</c>: <see cref="Microsoft.EntityFrameworkCore.RelationalQueryableExtensions"/>
/// deletes are set-based and bypass the change tracker, so the cascade must exist in the schema
/// (it does for every relation a purge in this repository relies on).
/// </para>
/// </summary>
public static class RetentionPurge
{
    /// <summary>
    /// Deletes every row matching <paramref name="predicate"/> in batches of at most
    /// <paramref name="batchSize"/>, and returns the total removed.
    /// </summary>
    /// <typeparam name="TEntity">The entity being purged.</typeparam>
    /// <typeparam name="TKey">Its primary key type (the batch is addressed by key, never by offset).</typeparam>
    /// <param name="set">The owning module's <see cref="DbSet{TEntity}"/>.</param>
    /// <param name="predicate">The retention rule. Must be expressible in SQL over one table.</param>
    /// <param name="keySelector">Selects the key used to address the claimed batch.</param>
    /// <param name="batchSize">Maximum rows per statement; must be positive.</param>
    /// <param name="cancellationToken">Cancels between batches and inside each statement.</param>
    public static Task<int> DeleteInBatchesAsync<TEntity, TKey>(
        DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TKey>> keySelector,
        int batchSize,
        CancellationToken cancellationToken = default)
        where TEntity : class =>
        DeleteInBatchesAsync(set, predicate, keySelector, beforeDelete: null, batchSize, cancellationToken);

    /// <summary>
    /// The same bounded delete with a hook that runs for each claimed batch <b>before</b> its rows are
    /// removed — the place for an external effect that must not outlive the row naming it (a playback
    /// session's transcode directory, for instance).
    /// </summary>
    /// <param name="beforeDelete">
    /// Runs outside any transaction, so it must be idempotent and must not throw for an effect that is
    /// already gone. Ordering is deliberate: an interruption between the effect and the delete leaves
    /// the rows in place and the next pass claims them again, whereas deleting first would strand the
    /// external state with nothing left to name it.
    /// </param>
    public static async Task<int> DeleteInBatchesAsync<TEntity, TKey>(
        DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TKey>> keySelector,
        Func<IReadOnlyList<TKey>, CancellationToken, Task>? beforeDelete,
        int batchSize,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Claim the batch by key first. Selecting keys and deleting by key keeps the delete
            // statement short and its plan trivial, and it never depends on provider support for
            // LIMIT inside DELETE.
            var keys = await set
                .AsNoTracking()
                .Where(predicate)
                .Select(keySelector)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (keys.Count == 0)
            {
                return total;
            }

            if (beforeDelete is not null)
            {
                await beforeDelete(keys, cancellationToken);
            }

            // The predicate is re-applied in the delete itself, not just in the claim. Claiming and
            // deleting are two statements, and a retention rule is not always purely time-based: a
            // row that stops being eligible in between (an administrator confirming a manual override
            // on an old evaluation, say) must survive. Progress is still guaranteed, because a row
            // that stops matching also stops being claimed.
            var deleted = await set
                .Where(predicate)
                .Where(KeyIn(keySelector, keys))
                .ExecuteDeleteAsync(cancellationToken);

            total += deleted;

            if (deleted == 0 || keys.Count < batchSize)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// The same bounded loop for a retention rule that <b>blanks a column</b> rather than removing the
    /// row — used where the row itself is an audit record that must survive but one of its columns is
    /// dead weight (a finished download's resume blob, for instance).
    /// </summary>
    /// <param name="setters">
    /// Must falsify <paramref name="predicate"/> for the rows it touches, so each pass strictly
    /// shrinks the matching set. The loop also stops as soon as a claimed batch updates nothing, so a
    /// rule that does not shrink stops instead of spinning.
    /// </param>
    public static async Task<int> UpdateInBatchesAsync<TEntity, TKey>(
        DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TKey>> keySelector,
        Action<UpdateSettersBuilder<TEntity>> setters,
        int batchSize,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(setters);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var keys = await set
                .AsNoTracking()
                .Where(predicate)
                .Select(keySelector)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (keys.Count == 0)
            {
                return total;
            }

            // Same reason as the delete: the claim and the update are two statements, so the rule is
            // re-checked where it is applied rather than trusted from a moment ago.
            var updated = await set
                .Where(predicate)
                .Where(KeyIn(keySelector, keys))
                .ExecuteUpdateAsync(setters, cancellationToken);

            total += updated;

            if (updated == 0 || keys.Count < batchSize)
            {
                return total;
            }
        }
    }

    /// <summary>Builds <c>entity =&gt; keys.Contains(keySelector(entity))</c> from the caller's selector.</summary>
    private static Expression<Func<TEntity, bool>> KeyIn<TEntity, TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        List<TKey> keys)
    {
        var contains = Expression.Call(
            Expression.Constant(keys),
            ContainsMethod<TKey>(),
            keySelector.Body);

        return Expression.Lambda<Func<TEntity, bool>>(contains, keySelector.Parameters);
    }

    private static System.Reflection.MethodInfo ContainsMethod<TKey>() =>
        typeof(List<TKey>).GetMethod(nameof(List<TKey>.Contains), [typeof(TKey)])!;
}
