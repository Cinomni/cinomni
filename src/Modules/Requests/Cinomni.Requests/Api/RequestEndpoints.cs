using System.Security.Claims;
using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Requests.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Requests.Api;

/// <summary>
/// HTTP surface of the Requests module: an account with the request permission submits, everyone browses
/// their own, and an administrator decides. A non-administrator only ever sees their own requests, and
/// whether a submission skips the queue comes from the session's permissions — both are read from the
/// claims, never from a client-supplied parameter.
/// </summary>
public static class RequestEndpoints
{
    private const int DefaultLimit = 100;

    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/requests").RequireAuthorization();

        group.MapGet("/", async (
            MediaRequestStatus? status,
            bool? mine,
            int? limit,
            ClaimsPrincipal principal,
            IMediaRequestQuery query,
            IContentAccess access,
            CancellationToken cancellationToken) =>
        {
            var callerId = CallerId(principal);
            if (callerId is null || Viewer.From(principal) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            // Operators see everyone's requests unless they ask for their own; everyone else is scoped
            // to themselves — the caller cannot widen this from the query string.
            var requester = IsAdministrator(principal) && mine != true ? (Guid?)null : callerId;

            var requests = await query.ListAsync(status, requester, limit ?? DefaultLimit, cancellationToken);
            var visible = await VisibleWorksAsync(access, viewer, requests, cancellationToken);
            return Results.Ok(requests.Select(request => ToDto(AsSeenBy(request, visible))));
        });

        group.MapGet("/pending-count", async (IMediaRequestQuery query, CancellationToken cancellationToken) =>
            Results.Ok(new { count = await query.PendingCountAsync(cancellationToken) }))
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/", async (
            SubmitRequest request,
            ClaimsPrincipal principal,
            IMediaRequestCommands commands,
            CancellationToken cancellationToken) =>
        {
            var userId = CallerId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            if (ParseKind(request.Kind) is not { } kind)
            {
                return Results.Json(
                    new { error = "requests.invalid_kind", message = "A request is for a Movie or a Series." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var submission = new SubmitMediaRequest(
                request.Title,
                request.Year,
                request.Provider,
                request.ExternalId,
                userId.Value,
                principal.FindFirstValue(ClaimTypes.Name) ?? "unknown",
                AutoApprove: IsAdministrator(principal)
                    || principal.FindFirstValue(AuthorizationClaims.RequestsAutoApproved) == "true",
                // Absent means this account names no limit of its own and the installation default
                // applies. Read from the claim rather than looked up, because the claim is rebuilt
                // from the account on every request and cannot go stale.
                OpenRequestLimit: int.TryParse(
                    principal.FindFirstValue(AuthorizationClaims.OpenRequestLimit),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var openRequestLimit)
                    ? openRequestLimit
                    : null,
                Kind: kind,
                Requester: Viewer.From(principal));

            var result = await commands.SubmitAsync(submission, cancellationToken);
            return result.IsSuccess
                ? Results.Created($"/api/requests/{result.Value}", new { requestId = result.Value.ToString() })
                : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.CanRequest);

        group.MapPost("/{id:guid}/approve", async (
            Guid id,
            ClaimsPrincipal principal,
            IMediaRequestCommands commands,
            CancellationToken cancellationToken) =>
        {
            var userId = CallerId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var result = await commands.ApproveAsync(new MediaRequestId(id), userId.Value, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            RejectRequest request,
            ClaimsPrincipal principal,
            IMediaRequestCommands commands,
            CancellationToken cancellationToken) =>
        {
            var userId = CallerId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var result = await commands.RejectAsync(new MediaRequestId(id), userId.Value, request.Reason, cancellationToken);
            return result.IsSuccess ? Results.NoContent() : FailureResult(result.Error);
        })
            .RequireAuthorization(AuthorizationPolicies.Administrator);

        return endpoints;
    }

    /// <summary>
    /// The kind a submission names, Movie when it names none — which is what every client sent before the
    /// field existed. The numeric form is refused, since <c>Enum.TryParse</c> would accept any number.
    /// </summary>
    private static MediaRequestKind? ParseKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind)
            ? MediaRequestKind.Movie
            : Enum.TryParse<MediaRequestKind>(kind, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
                && !char.IsDigit(kind.Trim()[0])
                ? parsed
                : null;

    private static Guid? CallerId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static bool IsAdministrator(ClaimsPrincipal principal) =>
        principal.FindFirstValue(AuthorizationClaims.Administrator) == "true";

    private static IResult FailureResult(Error error)
    {
        var status = error.Code switch
        {
            "requests.not_found" => StatusCodes.Status404NotFound,
            "requests.duplicate" or "requests.already_catalogued" or "requests.already_decided" =>
                StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new { error = error.Code, message = error.Message }, statusCode: status);
    }

    /// <summary>The works linked to these requests that the viewer may see.</summary>
    private static async Task<IReadOnlySet<Guid>> VisibleWorksAsync(
        IContentAccess access, Viewer viewer, IReadOnlyList<MediaRequest> requests, CancellationToken cancellationToken)
    {
        var linked = requests.Select(r => r.WorkId).OfType<Guid>().Distinct().ToList();
        return await access.FilterWorksAsync(viewer, linked, cancellationToken);
    }

    /// <summary>
    /// A request linked to a work the viewer may not see is shown as decided and nothing more: no work id,
    /// and never "available". Approval does not grant access — the title can be on a shelf this person
    /// holds no grant for, or above their ceiling — and the request list must not say otherwise.
    /// </summary>
    internal static MediaRequest AsSeenBy(MediaRequest request, IReadOnlySet<Guid> visibleWorks) =>
        request.WorkId is { } workId && !visibleWorks.Contains(workId)
            ? request with
            {
                WorkId = null,
                Status = request.Status == MediaRequestStatus.Available ? MediaRequestStatus.Approved : request.Status,
            }
            : request;

    private static object ToDto(MediaRequest request) => new
    {
        id = request.Id.ToString(),
        title = request.Title,
        year = request.Year,
        provider = request.Provider,
        externalId = request.ExternalId,
        kind = request.Kind.ToString(),
        status = request.Status.ToString(),
        requestedByUserId = request.RequestedByUserId.ToString(),
        requestedByUsername = request.RequestedByUsername,
        workId = request.WorkId?.ToString(),
        decisionNote = request.DecisionNote,
        requestedAt = request.RequestedAt,
        decidedAt = request.DecidedAt,
    };

    public sealed record SubmitRequest(string Title, int? Year, string Provider, string ExternalId, string? Kind = null);

    public sealed record RejectRequest(string? Reason);
}
