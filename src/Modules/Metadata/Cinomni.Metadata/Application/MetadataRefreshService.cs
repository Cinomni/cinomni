using System.Text.Json;
using Cinomni.Kernel.Messaging;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Metadata.Application;

/// <summary>
/// Fetches a fresh metadata snapshot for a work from a provider and persists it, emitting
/// <c>MetadataRefreshed</c> (the ACL — Catalog copies the fields onto its own work). Over the fetched
/// artwork it runs the <see cref="ArtworkSelector"/> policy to pick the poster/backdrop, storing every
/// candidate. The provider fetch is out-of-process, so a failure backs off and emits
/// <c>MetadataRefreshFailed</c> (and <c>ProviderDegraded</c> once the provider crosses the threshold)
/// while the work persists without a fresh snapshot. Idempotent per (work, provider): a redelivered
/// trigger within the refresh TTL is a no-op. The snapshot and its events commit in one unit of work.
/// Only providers that support the requested <see cref="MetadataMediaKind"/> are consulted.
/// <para>
/// For a series the snapshot's season and episode children are attached <b>before</b> the snapshot is
/// added, so the existing cascade persists the whole structure atomically with the event; artwork
/// selection runs once per scope (series, then each season, then each episode still) so a season poster
/// cannot take the series poster slot; and the TTL is the short
/// <see cref="MetadataOptions.SeriesRefreshTtl"/> while the last snapshot reports the show as still
/// producing episodes.
/// </para>
/// </summary>
public sealed class MetadataRefreshService(
    MetadataDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    IEnumerable<IMetadataSource> sources,
    MetadataOptions options,
    ILogger<MetadataRefreshService> logger) : IMetadataRefresh
{
    public async Task RefreshAsync(
        Guid workId,
        string provider,
        string externalId,
        MetadataMediaKind kind = MetadataMediaKind.Movie,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var state = await dbContext.RefreshStates
            .FirstOrDefaultAsync(s => s.WorkId == workId && s.Provider == provider, cancellationToken);
        var isNewState = state is null;
        state ??= RefreshState.Create(workId, provider, now);

        if (!state.ShouldRefresh(now, await RefreshTtlAsync(state, kind, cancellationToken)))
        {
            return; // fresh within TTL, already in progress, or backing off — nothing to do.
        }

        var source = sources.FirstOrDefault(s =>
            string.Equals(s.Name, provider, StringComparison.OrdinalIgnoreCase) && s.SupportedKinds.Contains(kind));
        if (source is null)
        {
            logger.LogWarning("No metadata source registered for provider '{Provider}' and kind {Kind}.", provider, kind);
            return;
        }

        state.BeginRefresh(now);

        ProviderMetadataResult? result = null;
        try
        {
            result = await source.FetchAsync(externalId, kind, cancellationToken);
        }
        // A body that is not the JSON the adapter expects is the provider failing, like a refused
        // connection: it takes the backoff and counts towards degradation. Escaping, it skipped both,
        // and the queue retried the command at once instead.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
            or JsonException)
        {
            logger.LogWarning(ex, "Metadata fetch failed for work {WorkId} ({Provider}:{ExternalId}).", workId, provider, externalId);
        }

        if (result is null)
        {
            state.MarkFailed(BackoffHorizon(state.Attempts, now), now);
            await PersistFailureAsync(state, isNewState, workId, provider, cancellationToken);
            return;
        }

        // Season posters and episode stills join the candidate set carrying their scope, and selection
        // runs once per scope — a flat run would let a season poster win the series poster slot.
        var candidates = SeriesSnapshotBuilder.ArtworkCandidates(result);
        var selection = ArtworkSelector.SelectScoped(candidates, ArtworkSelectionOptions.ForLanguage(options.Language));

        var snapshot = MetadataSnapshotRecord.Create(
            workId, provider, kind, result.ExternalId, result.Title, result.OriginalTitle, result.Year, result.Overview,
            result.RuntimeMinutes, result.OriginalLanguage,
            selection.PosterUrl ?? result.PosterUrl,
            selection.BackdropUrl ?? result.BackdropUrl,
            RawResponse.Clamp(result.RawJson), now,
            SeriesSnapshotBuilder.DetailsOf(result),
            result.Genres,
            result.ContentRating);

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            snapshot.Artwork.Add(MetadataArtworkRecord.Create(
                snapshot.Id, candidate, selection.IsSelected(i), i, candidate.SeasonNumber, candidate.EpisodeNumber));
        }

        // Attached before Add so the cascade writes seasons, episodes, artwork, the snapshot and the
        // event in a single transaction.
        if (result.Series is { } series)
        {
            SeriesSnapshotBuilder.AttachStructure(snapshot, series);
        }

        state.MarkFresh(snapshot.Id, now);
        await PersistSuccessAsync(state, isNewState, snapshot, new MetadataRefreshed(workId, snapshot.Id, provider), cancellationToken);
    }

    /// <summary>
    /// The TTL that decides whether a re-fetch is eligible. A series whose last snapshot reports it as
    /// still producing episodes uses the short series TTL; everything else uses the standard one. Only a
    /// series sitting in <c>Fresh</c> costs the extra lookup — the movie path is unchanged.
    /// </summary>
    private async Task<TimeSpan> RefreshTtlAsync(RefreshState state, MetadataMediaKind kind, CancellationToken cancellationToken)
    {
        if (kind != MetadataMediaKind.Series
            || state.Status != MetadataRefreshStatus.Fresh
            || state.SnapshotId is not { } snapshotId)
        {
            return options.RefreshTtl;
        }

        var status = await dbContext.Snapshots
            .Where(s => s.Id == snapshotId)
            .Select(s => s.SeriesStatus)
            .FirstOrDefaultAsync(cancellationToken);

        return status is SeriesStatus.Continuing or SeriesStatus.Upcoming ? options.SeriesRefreshTtl : options.RefreshTtl;
    }

    private DateTimeOffset BackoffHorizon(int attempts, DateTimeOffset now)
    {
        var minutes = options.BackoffBase.TotalMinutes * Math.Pow(2, Math.Max(0, attempts - 1));
        return now.AddMinutes(Math.Min(minutes, options.BackoffCap.TotalMinutes));
    }

    private async Task PersistFailureAsync(RefreshState state, bool isNewState, Guid workId, string provider, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async token =>
        {
            if (isNewState)
            {
                dbContext.RefreshStates.Add(state);
            }

            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new MetadataRefreshFailed(workId, provider, state.Attempts, "provider-unavailable"), token);

            // A provider that keeps failing crosses the degradation threshold — a separate signal for alerting.
            if (state.Attempts >= options.DegradeAfterAttempts)
            {
                await eventBus.PublishAsync(new ProviderDegraded(workId, provider, state.Attempts, "provider-unavailable"), token);
            }
        }, cancellationToken);

    private async Task PersistSuccessAsync(
        RefreshState state,
        bool isNewState,
        MetadataSnapshotRecord snapshot,
        MetadataRefreshed domainEvent,
        CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAsync(async token =>
        {
            if (isNewState)
            {
                dbContext.RefreshStates.Add(state);
            }

            // The snapshot is a new aggregate, so EF cascades its artwork children on Add.
            dbContext.Snapshots.Add(snapshot);
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(domainEvent, token);
        }, cancellationToken);
}
