using Cinomni.Decision.Contracts;
using Cinomni.Kernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Decision.Api;

/// <summary>HTTP surface of the Decision module: browse profiles and read why releases were accepted/rejected.</summary>
public static class DecisionEndpoints
{
    public static IEndpointRouteBuilder MapDecisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/decision").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/profiles", async (IProfileQuery query, CancellationToken cancellationToken) =>
            Results.Ok((await query.ListProfilesAsync(cancellationToken)).Select(p => new
            {
                id = p.Id.ToString(),
                name = p.Name,
                minFormatScore = p.MinFormatScore,
                cutoffRank = p.CutoffRank,
                upgradesAllowed = p.UpgradesAllowed,
            })));

        // Upgrades arrive switched off on a library that predates them — replacing files on a working
        // installation is the owner's call — so this is how they are switched on.
        group.MapPut("/profiles/{id:guid}/upgrade-policy", async (
            Guid id,
            UpgradePolicyRequest request,
            IProfileAdministration admin,
            CancellationToken cancellationToken) =>
        {
            var applied = await admin.SetUpgradePolicyAsync(
                id, request.CutoffRank, request.UpgradesAllowed, cancellationToken);

            return applied
                ? Results.NoContent()
                : Results.NotFound(new { error = "decision.profile_not_found", message = "No such acquisition profile." });
        });

        // The explainability surface: every release evaluated for a target, with reasons.
        group.MapGet("/targets/{targetId:guid}/evaluations",
            async (Guid targetId, IReleaseEvaluationQuery query, CancellationToken cancellationToken) =>
                Results.Ok((await query.GetForTargetAsync(targetId, cancellationToken)).Select(ToDto)));

        // Interactive search: run one now and read every candidate's verdict and reasons. It decides
        // nothing — the response is what a person chooses from.
        group.MapPost("/targets/{targetId:guid}/search", async (
            Guid targetId,
            IInteractiveSearch interactive,
            CancellationToken cancellationToken) =>
        {
            var result = await interactive.SearchAsync(targetId, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(ToDto(result.Value))
                : Results.Json(new { error = result.Error.Code, message = result.Error.Message },
                    statusCode: StatusCodes.Status404NotFound);
        });

        group.MapGet("/blocks", async (IReleaseBlocklist blocks, CancellationToken cancellationToken) =>
        {
            var list = await blocks.ListAsync(cancellationToken);
            return Results.Ok(new
            {
                blocks = list.Blocks.Select(ToDto),
                truncated = list.Truncated,
            });
        });

        group.MapPost("/evaluations/{evaluationId:guid}/block", async (
            Guid evaluationId,
            BlockReleaseRequest request,
            IReleaseBlocklist blocks,
            CancellationToken cancellationToken) =>
        {
            var result = await blocks.BlockAsync(evaluationId, request.Reason, cancellationToken);
            if (result.IsSuccess)
            {
                return Results.Ok(ToDto(result.Value));
            }

            var status = result.Error.Code == "decision.evaluation_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return Results.Json(new { error = result.Error.Code, message = result.Error.Message }, statusCode: status);
        });

        group.MapDelete("/blocks/{id:guid}", async (
            Guid id, IReleaseBlocklist blocks, CancellationToken cancellationToken) =>
        {
            await blocks.UnblockAsync(id, cancellationToken);
            return Results.NoContent();
        });

        // The manual override: acquire this exact release, whatever the profile made of it.
        group.MapPost("/evaluations/{evaluationId:guid}/grab", async (
            Guid evaluationId,
            IInteractiveSearch interactive,
            CancellationToken cancellationToken) =>
        {
            var result = await interactive.GrabAsync(evaluationId, cancellationToken);
            if (result.IsSuccess)
            {
                return Results.Accepted(value: ToDto(result.Value));
            }

            var status = result.Error.Code == "decision.evaluation_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return Results.Json(new { error = result.Error.Code, message = result.Error.Message }, statusCode: status);
        });

        return endpoints;
    }

    private static object ToDto(InteractiveSearchResult result) => new
    {
        targetId = result.TargetId.ToString(),
        workId = result.WorkId.ToString(),
        searchId = result.SearchId.ToString(),
        term = result.Term,
        label = result.Label,
        candidates = result.Candidates.Select(ToDto),
    };

    private static object ToDto(EvaluatedCandidate candidate) => new
    {
        evaluationId = candidate.EvaluationId.ToString(),
        releaseGuid = candidate.ReleaseGuid,
        releaseTitle = candidate.ReleaseTitle,
        indexerName = candidate.IndexerName,
        protocol = candidate.Protocol,
        sizeBytes = candidate.SizeBytes,
        seeders = candidate.Seeders,
        leechers = candidate.Leechers,
        publishedAt = candidate.PublishedAt,
        verdict = candidate.Verdict.ToString(),
        customFormatScore = candidate.CustomFormatScore,
        episodeCoverage = candidate.EpisodeCoverage,
        isRecommended = candidate.IsRecommended,
        reasons = candidate.Reasons.Select(ToDto),
        blocked = candidate.Blocked,
        blockId = candidate.BlockId?.ToString(),
        blockReason = candidate.BlockReason,
        grabOverridesVerdict = candidate.GrabOverridesVerdict,
    };

    private static object ToDto(ReleaseBlock block) => new
    {
        id = block.Id.ToString(),
        releaseGuid = block.ReleaseGuid,
        releaseTitle = block.ReleaseTitle,
        reason = block.Reason,
        createdAt = block.CreatedAt,
    };

    private static object ToDto(ManualSelection selection) => new
    {
        evaluationId = selection.EvaluationId.ToString(),
        targetId = selection.TargetId.ToString(),
        releaseTitle = selection.ReleaseTitle,
        verdict = selection.Verdict.ToString(),
        overrodeVerdict = selection.OverrodeVerdict,
    };

    private static object ToDto(EvaluationReason reason) => new
    {
        rule = reason.Rule,
        property = reason.Property,
        profileValue = reason.ProfileValue,
        actualValue = reason.ActualValue,
        outcome = reason.Outcome.ToString(),
        rejection = reason.Rejection?.ToString(),
    };

    /// <summary>How good is good enough, and whether to look for better at all.</summary>
    private sealed record UpgradePolicyRequest(int CutoffRank, bool UpgradesAllowed);

    private sealed record BlockReleaseRequest(string Reason);

    private static object ToDto(ReleaseEvaluationSummary evaluation) => new
    {
        id = evaluation.Id.ToString(),
        releaseGuid = evaluation.ReleaseGuid,
        releaseTitle = evaluation.ReleaseTitle,
        verdict = evaluation.Verdict.ToString(),
        customFormatScore = evaluation.CustomFormatScore,
        reasons = evaluation.Reasons.Select(ToDto),
        indexerName = evaluation.IndexerName,
        seeders = evaluation.Seeders,
        leechers = evaluation.Leechers,
    };
}
