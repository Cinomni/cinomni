using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Catalog.Api;

/// <summary>
/// HTTP surface of rule-based placement, administrator only: a collection's rules, the order collections
/// are asked in, a preview of each before it is saved, and releasing a manual pin. Every write can change
/// who may see a title, which is why each one has a preview route beside it that writes nothing.
/// </summary>
internal static class CollectionRuleEndpoints
{
    public static void MapCollectionRuleRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/collections/{id:guid}/rules", async (
            Guid id,
            ICollectionRules rules,
            CancellationToken cancellationToken) =>
        {
            var result = await rules.RulesAsync(new CollectionId(id), cancellationToken);
            return result.IsSuccess
                ? Results.Ok(result.Value.Select(ToDto))
                : CatalogEndpoints.FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/collections/{id:guid}/rules", async (
            Guid id,
            List<CollectionRuleRequest?>? request,
            ICollectionRules rules,
            CancellationToken cancellationToken) =>
        {
            var result = await rules.SetRulesAsync(new CollectionId(id), ToDrafts(request), cancellationToken);
            return result.IsSuccess
                ? Results.Ok(new { moved = result.Value.Moved })
                : CatalogEndpoints.FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/collections/{id:guid}/rules/preview", async (
            Guid id,
            List<CollectionRuleRequest?>? request,
            ICollectionRules rules,
            CancellationToken cancellationToken) =>
        {
            var result = await rules.PreviewRulesAsync(new CollectionId(id), ToDrafts(request), cancellationToken);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value)) : CatalogEndpoints.FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPut("/collections/rule-priority", async (
            RulePriorityRequest? request,
            ICollectionRules rules,
            CancellationToken cancellationToken) =>
        {
            var result = await rules.SetRulePriorityAsync(ToOrder(request), cancellationToken);
            return result.IsSuccess
                ? Results.Ok(new { moved = result.Value.Moved })
                : CatalogEndpoints.FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/collections/rule-priority/preview", async (
            RulePriorityRequest? request,
            ICollectionRules rules,
            CancellationToken cancellationToken) =>
        {
            var result = await rules.PreviewRulePriorityAsync(ToOrder(request), cancellationToken);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value)) : CatalogEndpoints.FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapDelete("/works/{id:guid}/collection-pin", async (
            Guid id,
            ICollectionRules rules,
            CancellationToken cancellationToken) =>
        {
            var result = await rules.UnpinWorkAsync(new WorkId(id), cancellationToken);
            return result.IsSuccess ? Results.NoContent() : CatalogEndpoints.FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);
    }

    /// <summary>
    /// Normalises the body at the boundary: a missing list, a null rule, a null condition or a null value
    /// list all become empty rather than reaching the validator as nulls. An id that is not a GUID — the
    /// web client sends an empty one for a rule it has not saved yet — means a new rule.
    /// </summary>
    private static IReadOnlyList<CollectionRuleDraft> ToDrafts(List<CollectionRuleRequest?>? request) =>
        [.. (request ?? []).OfType<CollectionRuleRequest>().Select(rule => new CollectionRuleDraft(
            Guid.TryParse(rule.Id, out var id) ? id : null,
            rule.Name ?? string.Empty,
            [.. (rule.Conditions ?? []).OfType<CollectionRuleConditionRequest>().Select(condition =>
                new CollectionRuleCondition(condition.Field, condition.Operator, [.. condition.Values ?? []]))]))];

    private static IReadOnlyList<CollectionId> ToOrder(RulePriorityRequest? request) =>
        [.. (request?.CollectionIds ?? []).Select(id => new CollectionId(id))];

    private static object ToDto(CollectionRule rule) => new
    {
        id = rule.Id.ToString(),
        collectionId = rule.CollectionId.ToString(),
        name = rule.Name,
        conditions = rule.Conditions.Select(condition => new
        {
            field = condition.Field,
            @operator = condition.Operator,
            values = condition.Values,
        }),
    };

    private static object ToDto(RulePreview preview) => new
    {
        matched = preview.Matched,
        wouldMove = preview.WouldMove,
        pinnedSkipped = preview.PinnedSkipped,
        works = preview.Works.Select(work => new
        {
            id = work.Id.ToString(),
            title = work.Title,
            year = work.Year,
            kind = work.Kind,
            currentCollectionId = work.CurrentCollectionId.ToString(),
            currentCollectionName = work.CurrentCollectionName,
            targetCollectionId = work.TargetCollectionId.ToString(),
            targetCollectionName = work.TargetCollectionName,
            pinned = work.Pinned,
        }),
    };

    public sealed record CollectionRuleRequest(string? Id, string? Name, List<CollectionRuleConditionRequest?>? Conditions);

    public sealed record CollectionRuleConditionRequest(
        CollectionRuleField Field,
        CollectionRuleOperator Operator,
        List<string>? Values);

    public sealed record RulePriorityRequest(List<Guid>? CollectionIds);
}
