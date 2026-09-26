using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Results;
using Cinomni.Kernel.Security;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Cinomni.Requests.Contracts;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Requests.Application;

/// <summary>
/// The write side of the Requests module: submit a request and decide on it. Submitting refuses titles
/// already in the library (→i Catalog) and titles that already have a live request; deciding is a guarded
/// transition on the record, so a repeated approval or rejection is a no-op rather than a second decision.
/// Each write and the event it publishes commit in one unit of work — the approval event is what
/// eventually puts the title in the catalog, never a direct write from here.
/// </summary>
public sealed class MediaRequestService(
    RequestsDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ICatalogQuery catalog,
    IContentAccess access,
    ILiveOptions<RequestQuotaOptions> quotas) : IMediaRequestCommands
{
    /// <summary>
    /// Refuses a submission from an account that already has as many requests open as it may.
    /// <para>
    /// Open is pending, or approved and not yet playable. A rejected request and a fulfilled one both
    /// release the slot: the cap is on how much of the administrator's decision queue one person may
    /// occupy at once, not on how much they may ask for over a lifetime. Counting a fulfilled request
    /// would make the limit only ever tighten, which is a different feature wearing this one's name.
    /// </para>
    /// <para>
    /// The message names the limit. An anonymous caller learning a throttle is one thing; a member
    /// being told "you have five of five open" is the difference between a rule and a wall.
    /// </para>
    /// </summary>
    private async Task<Result> CheckOpenRequestQuotaAsync(
        SubmitMediaRequest request, CancellationToken cancellationToken)
    {
        // The account's own limit if it names one, the installation's otherwise; zero means no limit,
        // which is what an administrator carries and what every installation ships with.
        var limit = request.OpenRequestLimit ?? quotas.Current.DefaultOpenRequestLimit;
        if (limit <= 0)
        {
            return Result.Success();
        }

        var open = await dbContext.MediaRequests.CountAsync(
            r => r.RequestedByUserId == request.RequestedByUserId
                && (r.Status == MediaRequestStatus.Pending || r.Status == MediaRequestStatus.Approved),
            cancellationToken);

        return open < limit
            ? Result.Success()
            : Result.Failure(new Error(
                "requests.quota_exceeded",
                $"You already have {open} of {limit} requests open. One has to be decided or arrive before you can ask for another."));
    }

    public async Task<Result<MediaRequestId>> SubmitAsync(
        SubmitMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Result<MediaRequestId>.Failure(new Error("requests.invalid_title", "A title is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ExternalId))
        {
            return Result<MediaRequestId>.Failure(new Error(
                "requests.invalid_reference", "A request must point at a provider and its external id."));
        }

        var quota = await CheckOpenRequestQuotaAsync(request, cancellationToken);
        if (quota.IsFailure)
        {
            return Result<MediaRequestId>.Failure(quota.Error);
        }

        var record = MediaRequestRecord.Create(request, DateTimeOffset.UtcNow);

        // The provider must be one Catalog can key a work on. Accepting an unknown name would leave the
        // request unlinkable: the already-in-library check below could not run, and fulfilment would
        // catalogue a work with no external id — which is what makes that command idempotent.
        var provider = Providers.Parse(record.Provider);
        if (provider is null)
        {
            return Result<MediaRequestId>.Failure(new Error(
                "requests.invalid_reference", $"'{request.Provider}' is not a metadata provider we can link a title to."));
        }

        // "Already in your library" only when it is in theirs. A title that exists but is hidden from
        // the requester answers like one that does not: the request goes to the administrator, whose
        // decision it is whether this person gets it — refusing here would tell a child that the title
        // their ceiling hides is on the shelf.
        var workKind = record.Kind == MediaRequestKind.Series ? WorkKind.Series : WorkKind.Movie;
        var hiddenFromRequester = false;
        if (await catalog.FindByExternalIdAsync(provider.Value, record.ExternalId, workKind, cancellationToken) is { } existing)
        {
            // No requester means nobody to answer "it is in your library" for: the title counts as hidden,
            // which only ever routes the request to the administrator.
            if (request.Requester is { } requester
                && await access.CanSeeWorkAsync(requester, existing.Id.Value, cancellationToken))
            {
                return Result<MediaRequestId>.Failure(new Error(
                    "requests.already_catalogued", "That title is already in your library."));
            }

            hiddenFromRequester = true;
        }

        // One live request per title (the partial unique index is the backstop); a rejected one may be
        // submitted again.
        var alreadyRequested = await dbContext.MediaRequests.AnyAsync(
            r => r.Provider == record.Provider
                && r.ExternalId == record.ExternalId
                && r.Kind == record.Kind
                && r.Status != MediaRequestStatus.Rejected,
            cancellationToken);

        if (alreadyRequested)
        {
            return Result<MediaRequestId>.Failure(new Error(
                "requests.duplicate", "That title has already been requested."));
        }

        // A trusted requester decides their own: the request is created already approved, and both events
        // commit with it, so fulfilment starts from the same transaction an operator's approval would.
        // Never for a title that is already here but hidden from them: approving links the request to that
        // work, and "decide your own" is not "grant yourself what the household keeps from you".
        var approved = request.AutoApprove
            && !hiddenFromRequester
            && record.Approve(record.RequestedByUserId, record.RequestedAt);

        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                dbContext.MediaRequests.Add(record);
                await dbContext.SaveChangesAsync(token);
                await eventBus.PublishAsync(
                    new MediaRequested(
                        record.Id, record.Title, record.Year, record.Provider, record.ExternalId,
                        record.RequestedByUserId, record.RequestedByUsername),
                    token);

                if (approved)
                {
                    await eventBus.PublishAsync(
                        new MediaRequestApproved(
                            record.Id, record.Title, record.Year, record.Provider, record.ExternalId,
                            record.RequestedByUserId),
                        token);
                }
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsAlreadyRequested(ex))
        {
            // Two people asked for the same title at once: the partial unique index caught the loser of
            // the race. Same answer as the check above, rather than surfacing a 500.
            dbContext.Entry(record).State = EntityState.Detached;
            return Result<MediaRequestId>.Failure(new Error(
                "requests.duplicate", "That title has already been requested."));
        }

        return Result<MediaRequestId>.Success(new MediaRequestId(record.Id));
    }

    public async Task<Result> ApproveAsync(
        MediaRequestId id,
        Guid decidedByUserId,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.MediaRequests.FirstOrDefaultAsync(r => r.Id == id.Value, cancellationToken);
        if (record is null)
        {
            return Result.Failure(new Error("requests.not_found", "No such request."));
        }

        // Already decided: approving again is a no-op, rejecting-then-approving is a conflict.
        if (!record.Approve(decidedByUserId, DateTimeOffset.UtcNow))
        {
            return record.Status == MediaRequestStatus.Rejected
                ? Result.Failure(new Error("requests.already_decided", "That request was already rejected."))
                : Result.Success();
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(
                new MediaRequestApproved(
                    record.Id, record.Title, record.Year, record.Provider, record.ExternalId, record.RequestedByUserId),
                token);
        }, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> RejectAsync(
        MediaRequestId id,
        Guid decidedByUserId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var record = await dbContext.MediaRequests.FirstOrDefaultAsync(r => r.Id == id.Value, cancellationToken);
        if (record is null)
        {
            return Result.Failure(new Error("requests.not_found", "No such request."));
        }

        if (!record.Reject(decidedByUserId, reason, DateTimeOffset.UtcNow))
        {
            return record.Status == MediaRequestStatus.Rejected
                ? Result.Success()
                : Result.Failure(new Error("requests.already_decided", "That request was already approved."));
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(
                new MediaRequestRejected(record.Id, record.Title, record.RequestedByUserId, record.DecisionNote),
                token);
        }, cancellationToken);

        return Result.Success();
    }

    /// <summary>True when the failure is the live-request uniqueness violation (a concurrent submit).</summary>
    private static bool IsAlreadyRequested(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
