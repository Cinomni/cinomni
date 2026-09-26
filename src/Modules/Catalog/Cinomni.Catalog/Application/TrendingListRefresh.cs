using Cinomni.Catalog.Contracts;
using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Metadata.Contracts;
using Cinomni.Operations.Settings;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Catalog.Application;

/// <summary>
/// Adds titles from the trending list that Catalog does not already know. Opt-in, and idempotent:
/// a title already present by external id is recorded and left alone, and running the job twice does
/// not create a second work. The provider call happens before any write.
/// </summary>
public sealed class TrendingListRefresh(
    CatalogDbContext dbContext,
    IUnitOfWork unitOfWork,
    ICatalogCommands commands,
    ICatalogQuery catalog,
    IMetadataLists lists,
    IMetadataRefresh refresh,
    ILiveOptions<TrendingListOptions> options,
    ILogger<TrendingListRefresh> logger)
{
    public const int MaxPerKind = 20;
    public const string Added = "Added";
    public const string AlreadyKnown = "AlreadyKnown";

    /// <summary>An administrator removed the work this entry added; the list never adds it again.</summary>
    public const string Removed = "Removed";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Current.Enabled)
        {
            return;
        }

        var movies = await lists.TrendingAsync(MetadataMediaKind.Movie, MaxPerKind, cancellationToken);
        var series = await lists.TrendingAsync(MetadataMediaKind.Series, MaxPerKind, cancellationToken);

        foreach (var title in movies.Concat(series))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RememberAsync(title, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One title's failure is that title's: the rest of the list still gets its turn, and a
                // half-written change must not ride along on the next title's save.
                dbContext.ChangeTracker.Clear();
                logger.LogWarning(ex, "Trending list could not record '{Title}'.", title.Title);
            }
        }
    }

    private async Task RememberAsync(TrendingTitle title, CancellationToken cancellationToken)
    {
        if (!TryProvider(title.Provider, out var provider) || string.IsNullOrWhiteSpace(title.ExternalId))
        {
            return;
        }

        var externalId = title.ExternalId.Trim();
        var kind = title.Kind == MetadataMediaKind.Series ? WorkKind.Series : WorkKind.Movie;
        if (await dbContext.ImportListEntries.AnyAsync(
                e => e.Provider == title.Provider && e.ExternalId == externalId && e.Kind == kind.ToString() && e.Outcome == Removed,
                cancellationToken))
        {
            return;
        }

        var known = await catalog.FindByExternalIdAsync(provider, externalId, kind, cancellationToken);
        Guid workId;
        string outcome;

        if (known is not null)
        {
            workId = known.Id.Value;
            outcome = AlreadyKnown;
        }
        else
        {
            // Catalogued, not monitored: a list is a suggestion, not a request. Watching every title it
            // names would start up to forty downloads each time the job runs; an operator monitors the
            // ones worth having, and a household member's approved request does the same.
            ExternalId[] ids = [new ExternalId(provider, externalId)];
            var added = kind == WorkKind.Series
                ? await commands.AddSeriesAsync(title.Title, title.Year, ids, monitored: false, cancellationToken: cancellationToken)
                : await commands.AddMovieAsync(title.Title, title.Year, ids, monitored: false, cancellationToken: cancellationToken);
            if (added.IsFailure)
            {
                logger.LogWarning("Trending list skipped '{Title}': {Reason}.", title.Title, added.Error.Message);
                return;
            }

            workId = added.Value.Value;
            outcome = Added;
        }

        // The entry is written before the provider is asked for anything, so a work this job created is
        // recorded as its own even when enrichment then fails.
        await UpsertAsync(title, externalId, kind, workId, outcome, cancellationToken);

        // Asked on every run for every title on the list, not only on the run that added it: a failure
        // that time — of the provider, or of this job before it got here — left a bare work that "already
        // known" would otherwise never enrich. Within its freshness window the refresh is a no-op, so
        // asking again costs a read.
        await EnrichAsync(workId, title, externalId, cancellationToken);
    }

    private async Task EnrichAsync(Guid workId, TrendingTitle title, string externalId, CancellationToken cancellationToken)
    {
        try
        {
            await refresh.RefreshAsync(workId, title.Provider, externalId, title.Kind, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Trending list could not enrich work {WorkId} ('{Title}'); the next run retries.", workId, title.Title);
        }
    }

    private async Task UpsertAsync(
        TrendingTitle title,
        string externalId,
        WorkKind kind,
        Guid workId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = await dbContext.ImportListEntries.FirstOrDefaultAsync(
            e => e.Provider == title.Provider && e.ExternalId == externalId && e.Kind == kind.ToString(),
            cancellationToken);

        if (entry is null)
        {
            entry = new ImportListEntry
            {
                Id = Uuid7.New(),
                Provider = Text.Truncate(title.Provider, ImportListEntry.ProviderMaxLength)!,
                ExternalId = Text.Truncate(externalId, ImportListEntry.ExternalIdMaxLength)!,
                Kind = kind.ToString(),
                Title = Text.Truncate(title.Title.Trim(), ImportListEntry.TitleMaxLength)!,
                Year = title.Year,
                WorkId = workId,
                Outcome = outcome,
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            dbContext.ImportListEntries.Add(entry);
        }
        else
        {
            entry.Title = Text.Truncate(title.Title.Trim(), ImportListEntry.TitleMaxLength)!;
            entry.Year = title.Year;
            entry.WorkId = workId;
            entry.Outcome = entry.Outcome == Added ? Added : outcome;
            entry.LastSeenAt = now;
        }

        await unitOfWork.ExecuteAsync(async token => await dbContext.SaveChangesAsync(token), cancellationToken);
    }

    private static bool TryProvider(string name, out MetadataProvider provider)
    {
        if (string.Equals(name, "tmdb", StringComparison.OrdinalIgnoreCase))
        {
            provider = MetadataProvider.Tmdb;
            return true;
        }

        provider = default;
        return false;
    }
}
