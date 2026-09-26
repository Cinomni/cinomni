using Cinomni.Catalog.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Kernel.Results;
using Cinomni.Metadata.Contracts;
using Cinomni.Monitoring.Contracts;
using Cinomni.Operations.Transactions;
using Cinomni.Requests.Application;
using Cinomni.Requests.Contracts;
using Cinomni.Requests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Requests.Messaging;

/// <summary>
/// Fulfils an approved request: catalogues the title (→i Catalog — which is what makes Monitoring start
/// watching a new one, ⇠e <c>WorkAdded</c>), records the produced work on the request, switches on the
/// watch of a title that was already there unwatched (→i Monitoring), and only then pulls its metadata
/// snapshot so artwork and details fill in.
/// <para>
/// The catalog write happens through Catalog's own unit of work, so it runs <b>before</b> this module's —
/// units of work do not nest. That leaves a window where the work exists and the request does not know it
/// yet; a re-executed command closes it, because Catalog returns the existing work for a known external id
/// instead of minting a second one — which is why a request may not carry a provider Catalog
/// cannot key on (enforced at submit).
/// </para>
/// <para>
/// The link is written before enrichment, and enrichment cannot fault the command: an out-of-process
/// provider must never be able to undo a fulfilment that already happened. If the work is already playable
/// (an operator added and downloaded the same title while the request sat pending), <c>WorkAvailable</c>
/// has already fired and will not fire again, so the request is closed here rather than waiting forever.
/// </para>
/// </summary>
public sealed class FulfilMediaRequestCommandHandler(
    RequestsDbContext dbContext,
    IUnitOfWork unitOfWork,
    ICatalogCommands catalog,
    ICatalogQuery catalogQuery,
    IMetadataRefresh metadata,
    IMonitoringQuery monitoringQuery,
    IMonitoringCommands monitoring,
    ILogger<FulfilMediaRequestCommandHandler> logger) : ICommandHandler<FulfilMediaRequestCommand>
{
    public async Task<Result> HandleAsync(FulfilMediaRequestCommand command, CancellationToken cancellationToken = default)
    {
        var request = await dbContext.MediaRequests
            .FirstOrDefaultAsync(r => r.Id == command.RequestId, cancellationToken);

        // Unknown, or no longer approved (rejected before this ran) — nothing to fulfil.
        if (request is null || request.Status == MediaRequestStatus.Pending || request.Status == MediaRequestStatus.Rejected)
        {
            return Result.Success();
        }

        var provider = Providers.Parse(request.Provider);
        var externalIds = provider is null
            ? Array.Empty<ExternalId>()
            : [new ExternalId(provider.Value, request.ExternalId)];

        var isSeries = request.Kind == MediaRequestKind.Series;
        var added = isSeries
            ? await catalog.AddSeriesAsync(request.Title, request.Year, externalIds, collection: null, cancellationToken: cancellationToken)
            : await catalog.AddMovieAsync(request.Title, request.Year, externalIds, collection: null, cancellationToken: cancellationToken);
        if (added.IsFailure)
        {
            // Surface the error so the command is retried with backoff rather than silently dropped.
            logger.LogWarning("Could not catalogue request {RequestId}: {Error}.", request.Id, added.Error.Code);
            return Result.Failure(added.Error);
        }

        var workId = added.Value.Value;
        var attached = request.AttachWork(workId);

        // A work that already has an asset will never publish WorkAvailable again (Catalog only announces
        // the first transition), so close the request here instead of leaving it Approved forever.
        var work = await catalogQuery.GetByIdAsync(added.Value, cancellationToken);
        var playable = work?.HasAsset == true;
        var closed = playable && request.MarkAvailable();

        if (attached || closed)
        {
            await unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);
        }

        // Gated on the work, not on whether this run closed the request: a redelivered command finds the
        // request already Available, and must still not switch on a whole series for one playable file.
        if (!playable)
        {
            var watched = await EnsureMonitoredAsync(added.Value, request.Id, cancellationToken);
            if (watched.IsFailure)
            {
                return watched;
            }
        }

        // Enrichment is best-effort and out-of-process: Metadata already backs off on a provider failure
        // (emitting MetadataRefreshFailed), and anything that still escapes must not fault the command —
        // the title is catalogued, monitored and linked to the request either way.
        if (provider is not null)
        {
            try
            {
                await metadata.RefreshAsync(
                    workId,
                    request.Provider,
                    request.ExternalId,
                    isSeries ? MetadataMediaKind.Series : MetadataMediaKind.Movie,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Metadata enrichment failed while fulfilling request {RequestId}.", request.Id);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Makes sure something is looking for the title. A work this request just catalogued is watched by
    /// its own <c>WorkAdded</c>; one that was already there may not be — a list added it as a suggestion,
    /// or an operator switched it off — and an approved request is somebody asking for it. Applying the
    /// policy here also covers a work whose root does not exist yet: the default that arrives later only
    /// ever fills an empty slot, so it cannot switch this back off.
    /// </summary>
    private async Task<Result> EnsureMonitoredAsync(WorkId workId, Guid requestId, CancellationToken cancellationToken)
    {
        var root = await monitoringQuery.GetByWorkAsync(workId, cancellationToken);
        if (root is { Monitored: true })
        {
            return Result.Success();
        }

        var applied = await monitoring.ApplyMonitoringPolicyAsync(workId, MonitoringMode.All, cancellationToken);
        if (applied.IsFailure)
        {
            logger.LogWarning("Could not monitor the work of request {RequestId}: {Error}.", requestId, applied.Error.Code);
            return Result.Failure(applied.Error);
        }

        return Result.Success();
    }
}
