using Cinomni.Discovery.Contracts;
using Cinomni.Discovery.Persistence;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Net;
using Cinomni.Kernel.Results;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinomni.Discovery.Application;

/// <summary>
/// Subscribes, refreshes and removes catalog sources. The fetch runs outside any unit of work; its
/// outcome is persisted afterwards in one short transaction that holds a lock on the source row, so
/// two refreshes of the same source serialize instead of racing to replace the same snapshot.
/// </summary>
public sealed class IndexerCatalogSources(
    DiscoveryDbContext dbContext,
    IUnitOfWork unitOfWork,
    IIndexerCatalogFetcher fetcher,
    TimeProvider? timeProvider = null)
    : IIndexerCatalogSources
{
    public const string InvalidNameCode = "discovery.catalog_source.invalid_name";
    public const string InvalidUrlCode = "discovery.catalog_source.invalid_url";
    public const string DuplicateUrlCode = "discovery.catalog_source.duplicate_url";
    public const string NotFoundCode = "discovery.catalog_source.not_found";
    public const string RefreshedCode = "discovery.catalog_source.refreshed";

    private static readonly string LockSql =
        $"SELECT id AS \"Value\" FROM {DiscoveryDbContext.SchemaName}.indexer_catalog_sources WHERE id = {{0}} FOR UPDATE";

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<IndexerCatalogSourceSummary>> ListSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.IndexerCatalogSources
            .AsNoTracking()
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Select(s => new { Source = s, Count = dbContext.IndexerCatalogEntries.Count(e => e.SourceId == s.Id) })
            .ToListAsync(cancellationToken);
        return rows.Select(row => ToSummary(row.Source, row.Count)).ToList();
    }

    public async Task<Result<IndexerCatalogSourceSummary>> AddSourceAsync(
        string name,
        string url,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        if (normalizedName is null)
        {
            return Failure(InvalidNameCode,
                $"A source name must be 1 to {IndexerCatalogSource.NameMaxLength} printable characters.");
        }

        if (!TryNormalizeUrl(url, out var normalizedUrl))
        {
            // Counted, never quoted: a source URL may carry a token in its query string.
            NetworkGuardMetrics.RecordRejection(
                NetworkGuardMetrics.Reasons.InvalidUrl, CinomniTelemetry.Modules.Discovery);
            return Failure(InvalidUrlCode,
                $"A source URL must be an absolute https URL with a public host, at most {IndexerCatalogSource.UrlMaxLength} characters.");
        }

        if (await dbContext.IndexerCatalogSources.AsNoTracking().AnyAsync(s => s.Url == normalizedUrl, cancellationToken))
        {
            return DuplicateUrl();
        }

        var source = new IndexerCatalogSource
        {
            Id = Uuid7.New(), Name = normalizedName, Url = normalizedUrl, Enabled = true,
            CreatedAt = clock.GetUtcNow(),
        };

        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                dbContext.IndexerCatalogSources.Add(source);
                await dbContext.SaveChangesAsync(token);
            }, cancellationToken);
        }
        catch (DbUpdateException failure) when (failure.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return DuplicateUrl();
        }

        // Saved first, refreshed second: a source whose first fetch fails is still subscribed, with the
        // failure on it, and an interruption here leaves a source that simply has not been refreshed yet.
        return await RefreshSourceAsync(new IndexerCatalogSourceId(source.Id), cancellationToken);
    }

    public async Task<Result<IndexerCatalogSourceSummary>> RefreshSourceAsync(
        IndexerCatalogSourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        var url = await dbContext.IndexerCatalogSources.AsNoTracking()
            .Where(s => s.Id == sourceId.Value).Select(s => s.Url).SingleOrDefaultAsync(cancellationToken);
        if (url is null)
        {
            return NotFound();
        }

        // External I/O first, outside the transaction.
        var outcome = await FetchAndValidateAsync(new Uri(url), cancellationToken);
        var refreshedAt = clock.GetUtcNow();

        var persisted = false;
        await unitOfWork.ExecuteAsync(async token =>
        {
            var source = await LockAsync(sourceId.Value, token);
            if (source is null)
            {
                return;
            }

            if (outcome.IsSuccess)
            {
                await dbContext.IndexerCatalogEntries.Where(e => e.SourceId == source.Id).ExecuteDeleteAsync(token);
                // ExecuteDelete bypasses the change tracker: rows an earlier refresh in this scope saved
                // are still tracked under the same keys and would collide with their replacements.
                foreach (var stale in dbContext.ChangeTracker.Entries<IndexerCatalogSourceEntry>()
                             .Where(e => e.Entity.SourceId == source.Id).ToList())
                {
                    stale.State = EntityState.Detached;
                }

                dbContext.IndexerCatalogEntries.AddRange(outcome.Value.Select((entry, index) => ToRow(source.Id, index, entry)));
                source.RecordRefresh(refreshedAt, true, RefreshedCode,
                    $"Fetched {outcome.Value.Count} {(outcome.Value.Count == 1 ? "entry" : "entries")}.");
            }
            else
            {
                // The previous snapshot stays: a source that is down or published a broken manifest
                // keeps offering what it offered before.
                source.RecordRefresh(refreshedAt, false, outcome.Error.Code, outcome.Error.Message);
            }

            await dbContext.SaveChangesAsync(token);
            persisted = true;
        }, cancellationToken);

        if (!persisted)
        {
            // Removed while it was being fetched.
            return NotFound();
        }

        return await SummaryAsync(sourceId.Value, cancellationToken) is { } summary
            ? Result<IndexerCatalogSourceSummary>.Success(summary)
            : NotFound();
    }

    public async Task<Result<IndexerCatalogSourceSummary>> UpdateSourceAsync(
        IndexerCatalogSourceId sourceId,
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        if (normalizedName is null)
        {
            return Failure(InvalidNameCode,
                $"A source name must be 1 to {IndexerCatalogSource.NameMaxLength} printable characters.");
        }

        var source = await dbContext.IndexerCatalogSources.FirstOrDefaultAsync(s => s.Id == sourceId.Value, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        if (source.Name != normalizedName || source.Enabled != enabled)
        {
            source.Name = normalizedName;
            source.Enabled = enabled;
            await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        }

        return await SummaryAsync(sourceId.Value, cancellationToken) is { } summary
            ? Result<IndexerCatalogSourceSummary>.Success(summary)
            : NotFound();
    }

    public async Task<Result> DeleteSourceAsync(IndexerCatalogSourceId sourceId, CancellationToken cancellationToken = default)
    {
        var source = await dbContext.IndexerCatalogSources.FirstOrDefaultAsync(s => s.Id == sourceId.Value, cancellationToken);
        if (source is null)
        {
            return Result.Failure(new Error(NotFoundCode, "No catalog source with that id."));
        }

        // The snapshot cascades; installed indexers only lose the reference (ON DELETE SET NULL) and
        // keep running on the definition they snapshotted at install time.
        dbContext.IndexerCatalogSources.Remove(source);
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return Result.Success();
    }

    private async Task<Result<IReadOnlyList<CatalogManifestEntry>>> FetchAndValidateAsync(
        Uri url, CancellationToken cancellationToken)
    {
        var body = await fetcher.FetchAsync(url, cancellationToken);
        return body.IsFailure
            ? Result<IReadOnlyList<CatalogManifestEntry>>.Failure(body.Error)
            : IndexerCatalogManifest.Parse(body.Value);
    }

    private async Task<IndexerCatalogSource?> LockAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var locked = await dbContext.Database.SqlQueryRaw<Guid>(LockSql, sourceId).ToListAsync(cancellationToken);
        return locked.Count == 0
            ? null
            : await dbContext.IndexerCatalogSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
    }

    private async Task<IndexerCatalogSourceSummary?> SummaryAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var row = await dbContext.IndexerCatalogSources
            .AsNoTracking()
            .Where(s => s.Id == sourceId)
            .Select(s => new { Source = s, Count = dbContext.IndexerCatalogEntries.Count(e => e.SourceId == s.Id) })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToSummary(row.Source, row.Count);
    }

    private static IndexerCatalogSourceEntry ToRow(Guid sourceId, int position, CatalogManifestEntry entry) => new()
    {
        SourceId = sourceId, Key = entry.Key, Position = position, Version = entry.Version, Name = entry.Name,
        Description = entry.Description, ReleaseProtocol = entry.ReleaseProtocol, BaseUrls = [.. entry.BaseUrls],
        RequiresFlareSolverr = entry.RequiresFlareSolverr, DefaultPriority = entry.DefaultPriority,
        MinimumSeeders = entry.DefaultSettings.MinimumSeeders, PreferMagnet = entry.DefaultSettings.PreferMagnet,
        QueryLimit = entry.DefaultSettings.QueryLimit, GrabLimit = entry.DefaultSettings.GrabLimit,
        LimitsUnit = entry.DefaultSettings.LimitsUnit, UseFlareSolverr = entry.DefaultSettings.UseFlareSolverr,
        RawDefinition = entry.RawDefinition,
    };

    private static IndexerCatalogSourceSummary ToSummary(IndexerCatalogSource source, int entryCount) => new(
        new IndexerCatalogSourceId(source.Id), source.Name, source.Url, source.Enabled, source.CreatedAt,
        source.LastRefreshedAt, source.LastRefreshSucceeded, source.LastRefreshCode, source.LastRefreshMessage,
        entryCount);

    private static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > IndexerCatalogSource.NameMaxLength || trimmed.Any(char.IsControl)
            ? null
            : trimmed;
    }

    /// <summary>
    /// Https only, unlike an indexer base URL: a manifest decides which sites Cinomni will be told to
    /// query and how, so it must not be something a network in the middle can rewrite.
    /// </summary>
    internal static bool TryNormalizeUrl(string? url, out string normalized)
    {
        normalized = string.Empty;
        if (url is null || url.Length > IndexerCatalogSource.UrlMaxLength
            || !SsrfGuard.TryValidatePublicUrl(url, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.ToString();
        return normalized.Length <= IndexerCatalogSource.UrlMaxLength;
    }

    private static Result<IndexerCatalogSourceSummary> Failure(string code, string message) =>
        Result<IndexerCatalogSourceSummary>.Failure(new Error(code, message));

    private static Result<IndexerCatalogSourceSummary> NotFound() =>
        Failure(NotFoundCode, "No catalog source with that id.");

    private static Result<IndexerCatalogSourceSummary> DuplicateUrl() =>
        Failure(DuplicateUrlCode, "A catalog source with that URL is already subscribed.");
}
