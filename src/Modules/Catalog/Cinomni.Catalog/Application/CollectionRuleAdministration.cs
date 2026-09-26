using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Catalog.Application;

/// <summary>
/// The operator side of rule-based placement. Every write here commits the change together with a
/// queued sweep, then sweeps inline so the answer can say how many titles moved; if the process stops
/// part-way, the queued sweep finishes the job. Previews run the placement the sweep runs, against the
/// proposed rules, and write nothing.
/// </summary>
public sealed class CollectionRuleAdministration(
    CatalogDbContext dbContext,
    IUnitOfWork unitOfWork,
    CollectionPlacementService placement)
    : ICollectionRules
{
    /// <summary>A collection with more rules than this is one nobody can reason about; bounded like the conditions are.</summary>
    private const int MaxRulesPerCollection = 20;

    private static readonly Error CollectionNotFound = new("catalog.collection_not_found", "No such collection.");

    public async Task<Result<IReadOnlyList<CollectionRule>>> RulesAsync(
        CollectionId id,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Collections.AnyAsync(c => c.Id == id.Value, cancellationToken))
        {
            return Result<IReadOnlyList<CollectionRule>>.Failure(CollectionNotFound);
        }

        var rules = await dbContext.CollectionRules
            .AsNoTracking()
            .Where(r => r.CollectionId == id.Value)
            .OrderBy(r => r.Position)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<CollectionRule>>.Success([.. rules.Select(r => r.ToContract())]);
    }

    public async Task<Result<RulePreview>> PreviewRulesAsync(
        CollectionId id,
        IReadOnlyList<CollectionRuleDraft> rules,
        CancellationToken cancellationToken = default)
    {
        var current = await placement.LoadAsync(cancellationToken);
        var collection = current.Collections.FirstOrDefault(c => c.Id == id.Value);
        if (collection is null)
        {
            return Result<RulePreview>.Failure(CollectionNotFound);
        }

        var valid = Validate(rules);
        if (valid.IsFailure)
        {
            return Result<RulePreview>.Failure(valid.Error);
        }

        // The collection's stored rules are swapped for the drafts in place, so everything above and
        // below it in the order still gets its say — which is what decides where a displaced title lands.
        var drafted = rules
            .Select(draft => new RankedRule(draft.Id ?? Guid.Empty, collection.Id, collection.Kind, draft.Conditions))
            .ToList();
        var proposed = current.Collections
            .OrderBy(c => c.RulePriority)
            .ThenBy(c => c.Id)
            .SelectMany(c => c.Id == collection.Id ? drafted : current.Ordered.Where(r => r.CollectionId == c.Id))
            .ToList();

        return Result<RulePreview>.Success(await placement.PreviewAsync(current, proposed, drafted, cancellationToken));
    }

    public async Task<Result<RulesApplied>> SetRulesAsync(
        CollectionId id,
        IReadOnlyList<CollectionRuleDraft> rules,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Collections.AnyAsync(c => c.Id == id.Value, cancellationToken))
        {
            return Result<RulesApplied>.Failure(CollectionNotFound);
        }

        var valid = Validate(rules);
        if (valid.IsFailure)
        {
            return Result<RulesApplied>.Failure(valid.Error);
        }

        return Result<RulesApplied>.Success(await ApplyAsync(async token =>
        {
            // The collection row is locked before its rules are read, so two saves of the same set run
            // one after the other: the second replaces the first rather than both landing side by side.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM catalog.collections WHERE id = {id.Value} FOR UPDATE", token);
            await ReplaceRulesAsync(id.Value, rules, token);
        }, cancellationToken));
    }

    private async Task ReplaceRulesAsync(Guid collectionId, IReadOnlyList<CollectionRuleDraft> rules, CancellationToken cancellationToken)
    {
        var existing = await dbContext.CollectionRules
            .Where(r => r.CollectionId == collectionId)
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var kept = new HashSet<Guid>();
        for (var position = 0; position < rules.Count; position++)
        {
            var draft = rules[position];

            // An id that is not one of this collection's rules is a new rule, never a way to reach into
            // another collection's: the set being replaced is this one's and nothing else.
            if (draft.Id is { } ruleId && existing.TryGetValue(ruleId, out var stored))
            {
                stored.Update(draft.Name, position, draft.Conditions, now);
                kept.Add(ruleId);
            }
            else
            {
                dbContext.CollectionRules.Add(StoredCollectionRule.Create(collectionId, draft.Name, position, draft.Conditions, now));
            }
        }

        dbContext.CollectionRules.RemoveRange(existing.Values.Where(r => !kept.Contains(r.Id)));
    }

    public async Task<Result<RulePreview>> PreviewRulePriorityAsync(
        IReadOnlyList<CollectionId> order,
        CancellationToken cancellationToken = default)
    {
        var current = await placement.LoadAsync(cancellationToken);
        var complete = CheckComplete(order, current.Collections);
        if (complete.IsFailure)
        {
            return Result<RulePreview>.Failure(complete.Error);
        }

        var proposed = order
            .SelectMany(id => current.Ordered.Where(r => r.CollectionId == id.Value))
            .ToList();

        return Result<RulePreview>.Success(await placement.PreviewAsync(current, proposed, proposed, cancellationToken));
    }

    public async Task<Result<RulesApplied>> SetRulePriorityAsync(
        IReadOnlyList<CollectionId> order,
        CancellationToken cancellationToken = default)
    {
        var collections = await dbContext.Collections.ToListAsync(cancellationToken);
        var complete = CheckComplete(order, collections);
        if (complete.IsFailure)
        {
            return Result<RulesApplied>.Failure(complete.Error);
        }

        var byId = collections.ToDictionary(c => c.Id);
        for (var index = 0; index < order.Count; index++)
        {
            byId[order[index].Value].RulePriority = index;
        }

        return Result<RulesApplied>.Success(await ApplyAsync(_ => Task.CompletedTask, cancellationToken));
    }

    public async Task<Result> UnpinWorkAsync(WorkId workId, CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Works.AnyAsync(w => w.Id == workId.Value, cancellationToken))
        {
            return Result.Failure(new Error("catalog.work_not_found", "No such work."));
        }

        // Unpinning and re-placing are one unit of work, and the unpin is conditional on the row as it
        // stands: a title is never briefly unpinned on the old shelf, and a pin an operator takes at the
        // same moment waits on this row lock rather than being silently lost.
        await unitOfWork.ExecuteAsync(async token =>
        {
            var unpinned = await dbContext.Works
                .Where(w => w.Id == workId.Value && w.CollectionPinned)
                .ExecuteUpdateAsync(set => set.SetProperty(w => w.CollectionPinned, false), token);

            if (unpinned > 0)
            {
                await placement.PlaceExistingAsync(workId.Value, token);
            }
        }, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Commits the pending rule or order change with a queued sweep, then runs the sweep inline. The two
    /// are the same deterministic batches, so whichever runs second finds nothing left to move.
    /// </summary>
    private async Task<RulesApplied> ApplyAsync(Func<CancellationToken, Task> change, CancellationToken cancellationToken)
    {
        var runId = Uuid7.New();
        await unitOfWork.ExecuteAsync(async token =>
        {
            await change(token);
            await dbContext.SaveChangesAsync(token);
            await placement.QueueSweepAsync(runId, token);
        }, cancellationToken);

        return new RulesApplied(await placement.SweepAsync(runId, cancellationToken));
    }

    private static Result Validate(IReadOnlyList<CollectionRuleDraft> rules)
    {
        if (rules.Count > MaxRulesPerCollection)
        {
            return Result.Failure(new Error(
                "catalog.rule.too_many_rules", $"A collection may have at most {MaxRulesPerCollection} rules."));
        }

        var ids = rules.Where(r => r.Id is not null).Select(r => r.Id!.Value).ToList();
        if (ids.Count != ids.Distinct().Count())
        {
            return Result.Failure(new Error("catalog.rule.duplicate_id", "The same rule appears twice."));
        }

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Trim().Length > StoredCollectionRule.NameMax)
            {
                return Result.Failure(new Error(
                    "catalog.rule.invalid_name",
                    $"A rule needs a name of at most {StoredCollectionRule.NameMax} characters."));
            }

            var conditions = CollectionRuleValidator.Validate(rule.Conditions);
            if (conditions.IsFailure)
            {
                return conditions;
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// The order has to name every collection exactly once. A partial list would leave the missing ones
    /// at whatever value they had, tying with the ones that moved, and the tie-break would decide who sees
    /// what — so it is refused rather than guessed.
    /// </summary>
    private static Result CheckComplete(IReadOnlyList<CollectionId> order, IReadOnlyList<Collection> collections)
    {
        var named = order.Select(id => id.Value).ToHashSet();
        return named.Count == order.Count && named.SetEquals(collections.Select(c => c.Id))
            ? Result.Success()
            : Result.Failure(new Error(
                "catalog.rule_priority.incomplete", "The order must name every collection exactly once."));
    }
}
