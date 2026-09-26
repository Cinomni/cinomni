using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinomni.Downloads.Application;

/// <summary>Read model over the download tasks and their file/history trail (the Downloads read side).</summary>
public sealed class DownloadTaskQuery(DownloadsDbContext dbContext, TunnelOptions tunnel) : IDownloadQuery
{
    private static readonly DownloadState[] ActiveStates =
    [
        DownloadState.Queued, DownloadState.ResolvingMetadata, DownloadState.Checking,
        DownloadState.Downloading, DownloadState.Paused, DownloadState.Completed, DownloadState.Seeding,
    ];

    public async Task<IReadOnlyList<DownloadTaskSummary>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.Claims)
            .AsSplitQuery()
            .Where(t => ActiveStates.Contains(t.State))
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return tasks.Select(ToSummary).ToList();
    }

    public async Task<DownloadTaskDetail?> GetAsync(DownloadTaskId id, CancellationToken cancellationToken = default)
    {
        var task = await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.Files)
            .Include(t => t.History)
            .Include(t => t.Claims)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id.Value, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var files = task.Files
            .OrderBy(f => f.Index)
            .Select(f => new DownloadFileSummary(f.Index, f.Path, f.Size, f.Priority))
            .ToList();

        var history = task.History
            .OrderBy(h => h.Seq)
            .Select(h => new DownloadHistoryEntry(h.Seq, h.FromState, h.ToState, h.Trigger, h.OccurredAt, h.Note))
            .ToList();

        return new DownloadTaskDetail(ToSummary(task), files, history);
    }

    /// <summary>
    /// The persisted egress state and how many downloads it is currently holding. Answered from what
    /// the last observation wrote rather than by taking a new one: this is a read, and an operator
    /// refreshing a page must not be able to drive gRPC calls to the sidecar.
    /// </summary>
    public async Task<TunnelEgressStatus> GetTunnelStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await dbContext.TunnelState
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == TunnelStateRecord.SingletonId, cancellationToken);

        var held = await dbContext.Tasks
            .AsNoTracking()
            .CountAsync(t => t.NetworkHoldSince != null, cancellationToken);

        state ??= TunnelStateRecord.Initial(DateTimeOffset.UtcNow);
        return state.ToStatus(tunnel.IsConfigured, tunnel.LossPolicy, held);
    }

    private static DownloadTaskSummary ToSummary(DownloadTask task) => new(
        new DownloadTaskId(task.Id),
        task.InfoHash ?? string.Empty,
        task.Name,
        task.State,
        task.Progress,
        task.DownloadRate,
        task.UploadRate,
        task.NumPeers,
        task.NumSeeds,
        task.NetworkHoldSince is not null,
        GoalsServed(task));

    /// <summary>
    /// The originating goal first, then any that attached later, each once. The task's own
    /// <see cref="DownloadTask.IntentId"/> is included even without its claim row, so a task written
    /// before claims existed still names the goal it was created for.
    /// </summary>
    private static IReadOnlyList<Guid> GoalsServed(DownloadTask task) =>
        task.Claims
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.IntentId)
            .Prepend(task.IntentId)
            .Distinct()
            .ToList();
}
