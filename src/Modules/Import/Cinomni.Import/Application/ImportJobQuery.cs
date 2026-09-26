using Cinomni.Import.Contracts;
using Cinomni.Import.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Import.Application;

/// <summary>Read model over the import jobs and their file-operation/history trail.</summary>
public sealed class ImportJobQuery(ImportDbContext dbContext) : IImportQuery
{
    public async Task<IReadOnlyList<ImportJobSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        // The file count is a projected scalar on purpose: Include(Matches) here would turn the
        // job list into a cartesian product for every season pack in the history.
        var jobs = await dbContext.Jobs
            .AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new { Job = j, FileCount = j.Matches.Count })
            .ToListAsync(cancellationToken);
        return jobs.Select(j => ToSummary(j.Job, j.FileCount)).ToList();
    }

    public async Task<ImportJobDetail?> GetAsync(ImportJobId jobId, CancellationToken cancellationToken = default)
    {
        var job = await dbContext.Jobs
            .AsNoTracking()
            .Include(j => j.Matches)
            .Include(j => j.Operations)
            .Include(j => j.History)
            .FirstOrDefaultAsync(j => j.Id == jobId.Value, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var matches = job.Matches
            .OrderBy(m => m.Seq)
            .Select(m => new ImportFileMatchSummary(
                m.Seq,
                m.SourcePath,
                m.Size,
                m.TargetPath,
                m.AssetId,
                m.UnitIds,
                m.SeasonNumber,
                m.EpisodeNumbers,
                m.State,
                m.Reason))
            .ToList();

        var operations = job.Operations
            .OrderBy(o => o.Seq)
            .Select(o => new FileOperationSummary(o.Seq, o.Type, o.FromPath, o.ToPath, o.State, o.Verified))
            .ToList();

        var history = job.History
            .OrderBy(h => h.Seq)
            .Select(h => new ImportHistoryEntry(h.Seq, h.FromState, h.ToState, h.Trigger, h.OccurredAt, h.Note))
            .ToList();

        return new ImportJobDetail(ToSummary(job, matches.Count), job.MediaInfo, operations, history, matches);
    }

    private static ImportJobSummary ToSummary(ImportJob job, int fileCount) => new(
        new ImportJobId(job.Id),
        job.DownloadTaskId,
        job.IntentId,
        job.State,
        job.SourcePath,
        job.TargetPath,
        job.AssetId,
        job.Reason,
        fileCount);
}
