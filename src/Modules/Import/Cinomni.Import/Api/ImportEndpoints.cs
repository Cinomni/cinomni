using Cinomni.Import.Application;
using Cinomni.Import.Contracts;
using Cinomni.Import.Messaging;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Cinomni.Operations.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cinomni.Import.Api;

/// <summary>
/// HTTP surface of the Import module: observe the import jobs and their recoverable file operations.
/// The automatic spine — landing a completed download — is driven by <c>DownloadCompleted</c>, not by
/// API (manual import is a later addition).
/// </summary>
public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/imports").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/", async (IImportQuery query, CancellationToken cancellationToken) =>
            Results.Ok((await query.ListAsync(cancellationToken)).Select(ToSummaryDto)));

        // Before /{id:guid} would ever be reached for it — a literal segment cannot be a guid, but
        // keeping the two apart makes the routing obvious to the next reader.
        group.MapGet("/path-repair", async (LibraryPathRepair repair, CancellationToken cancellationToken) =>
            Results.Ok(ToRepairDto(await repair.PreviewAsync(cancellationToken))));

        group.MapPost("/path-repair", async (ICommandQueue commandQueue, CancellationToken cancellationToken) =>
        {
            // A run id per request: a deliberate second pass, after an operator has cleared whatever
            // blocked the first, must not be dropped as a duplicate of it.
            var runId = Uuid7.New();
            await commandQueue.EnqueueAsync(
                new RepairLibraryPathsCommand(runId),
                idempotencyKey: $"repair-library-paths:{runId}",
                cancellationToken);

            // Accepted, not Ok: the pass walks the library and moves files, so it runs on the queue
            // where a restart cannot abandon it half-way.
            return Results.Accepted(value: new { runId = runId.ToString() });
        });

        group.MapGet("/{id:guid}", async (Guid id, IImportQuery query, CancellationToken cancellationToken) =>
        {
            var detail = await query.GetAsync(new ImportJobId(id), cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(ToDetailDto(detail));
        });

        return endpoints;
    }

    /// <summary>
    /// The preview, as the operator reads it. Paths are included deliberately — this endpoint exists
    /// so someone can see exactly which files a repair would move, and it is administrator-only, the
    /// same bar the rest of this group carries.
    /// </summary>
    private static object ToRepairDto(PathRepairReport report) => new
    {
        entries = report.Entries.Select(entry => new
        {
            assetId = entry.AssetId.ToString(),
            from = entry.FromPath,
            to = entry.ToPath,
            outcome = entry.Outcome.ToString(),
            sidecars = entry.Sidecars,
        }),
        repairable = report.Entries.Count(e => e.Outcome is PathRepairOutcome.Repairable),
        blocked = report.Entries.Count(e => e.Outcome is PathRepairOutcome.Blocked),
    };

    private static object ToSummaryDto(ImportJobSummary job) => new
    {
        id = job.Id.ToString(),
        downloadTaskId = job.DownloadTaskId.ToString(),
        intentId = job.IntentId.ToString(),
        state = job.State.ToString(),
        sourcePath = job.SourcePath,
        targetPath = job.TargetPath,
        assetId = job.AssetId?.ToString(),
        reason = job.Reason,
        fileCount = job.FileCount,
    };

    private static object ToMatchDto(ImportFileMatchSummary match) => new
    {
        seq = match.Seq,
        sourcePath = match.SourcePath,
        size = match.Size,
        targetPath = match.TargetPath,
        assetId = match.AssetId.ToString(),
        unitIds = match.UnitIds.Select(u => u.ToString()),
        seasonNumber = match.SeasonNumber,
        episodeNumbers = match.EpisodeNumbers,
        state = match.State.ToString(),
        reason = match.Reason,
    };

    private static object ToDetailDto(ImportJobDetail detail) => new
    {
        job = ToSummaryDto(detail.Job),
        // The per-file trail: a season-pack job reporting one arbitrary targetPath is useless for
        // exactly the case the trail was built for.
        files = (detail.Matches ?? []).Select(ToMatchDto),
        mediaInfo = detail.MediaInfo is null ? null : new
        {
            container = detail.MediaInfo.Container,
            durationSeconds = detail.MediaInfo.DurationSeconds,
            bitrate = detail.MediaInfo.Bitrate,
            streams = detail.MediaInfo.Streams.Select(s => new
            {
                index = s.Index,
                kind = s.Kind.ToString(),
                codec = s.Codec,
                language = s.Language,
                width = s.Width,
                height = s.Height,
                channels = s.Channels,
                isDefault = s.IsDefault,
                isForced = s.IsForced,
            }),
        },
        operations = detail.Operations.Select(o => new
        {
            seq = o.Seq,
            type = o.Type.ToString(),
            from = o.FromPath,
            to = o.ToPath,
            state = o.State.ToString(),
            verified = o.Verified,
        }),
        history = detail.History.Select(h => new
        {
            seq = h.Seq,
            from = h.From.ToString(),
            to = h.To.ToString(),
            trigger = h.Trigger,
            occurredAt = h.OccurredAt,
            note = h.Note,
        }),
    };
}
