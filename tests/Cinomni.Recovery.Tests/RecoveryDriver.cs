using Cinomni.Acquisition.Contracts;
using Cinomni.Downloads.Application;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Import.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Persistence;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Recovery.Tests;

/// <summary>
/// Drives one service provider the way the Host drives itself — through the outbox relay and the
/// command worker — and reads the resulting rows back out of each module's own schema.
/// <para>
/// A driver belongs to one provider. After a restart the test builds a new one, which is deliberate:
/// nothing may be held across the restart boundary, because a leaked scope keeps the old provider's
/// pooled connection open and turns a recovery failure into an unrelated connection error.
/// </para>
/// </summary>
internal sealed class RecoveryDriver(ServiceProvider host)
{
    /// <summary>Alternates the command worker and the relay until nothing moves.</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            var commands = await DrainCommandsAsync();
            var events = await DrainOutboxAsync();
            if (commands == 0 && events == 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs the command worker and stops. This is how "committed but never published" is expressed:
    /// the handler's own writes and the events it queued are in the database, and the relay that would
    /// have delivered them never ran.
    /// </summary>
    public async Task<int> DrainCommandsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<CommandProcessor>();
        var total = 0;
        int processed;
        while ((processed = await processor.ProcessBatchAsync()) > 0)
        {
            total += processed;
        }

        return total;
    }

    /// <summary>Runs the outbox relay and stops.</summary>
    public async Task<int> DrainOutboxAsync()
    {
        await using var scope = host.CreateAsyncScope();
        var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
        var total = 0;
        int published;
        while ((published = await relay.ProcessBatchAsync()) > 0)
        {
            total += published;
        }

        return total;
    }

    // -- arranging -------------------------------------------------------------------------------

    /// <summary>
    /// Opens an acquisition goal and selects a release for it, exactly as Decision's hand-off does,
    /// then settles the chain so the download task exists.
    /// </summary>
    public async Task<(Guid IntentId, Guid AttemptId)> AcquireAsync(
        Guid targetId,
        Guid workId,
        string releaseGuid,
        string downloadUrl,
        IReadOnlyList<Guid>? unitIds = null)
    {
        await using (var scope = host.CreateAsyncScope())
        {
            var commands = scope.ServiceProvider.GetRequiredService<IAcquisitionCommands>();
            await commands.CreateIntentAsync(targetId, workId, "All", unitIds?.FirstOrDefault());
            await commands.SelectCandidateAsync(Uuid7.New(), targetId, releaseGuid, downloadUrl, unitIds);
        }

        await DrainAsync();

        await using var readScope = host.CreateAsyncScope();
        var query = readScope.ServiceProvider.GetRequiredService<IAcquisitionQuery>();
        var intent = await query.GetByTargetAsync(targetId);
        Assert.NotNull(intent);
        var detail = await query.GetAsync(intent.Id.Value);
        var attempt = Assert.Single(detail!.Attempts);
        return (intent.Id.Value, attempt.Id.Value);
    }

    /// <summary>Feeds one status snapshot into the module, exactly as the stream pump would.</summary>
    public async Task ApplyStatusAsync(TorrentSnapshot snapshot)
    {
        await using (var scope = host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>()
                .ApplyStatusAsync(snapshot.InfoHash, snapshot);
        }

        await DrainAsync();
    }

    /// <summary>Runs one checkpoint pass, which is what makes a task's resume data exist at all.</summary>
    public async Task SaveCheckpointsAsync()
    {
        await using (var scope = host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().SaveCheckpointsAsync();
        }

        await DrainAsync();
    }

    // -- reading ---------------------------------------------------------------------------------

    public async Task<List<DownloadTask>> DownloadTasksAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DownloadsDbContext>()
            .Tasks.AsNoTracking().Include(t => t.History).Include(t => t.Claims).Include(t => t.Units)
            .OrderBy(t => t.CreatedAt).ToListAsync();
    }

    public async Task<DownloadTask?> DownloadTaskAsync(Guid attemptId)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DownloadsDbContext>()
            .Tasks.AsNoTracking().Include(t => t.History)
            .FirstOrDefaultAsync(t => t.AttemptId == attemptId);
    }

    public async Task<List<ImportJob>> ImportJobsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ImportDbContext>()
            .Jobs.AsNoTracking().Include(j => j.Matches).Include(j => j.Operations).Include(j => j.History)
            .OrderBy(j => j.CreatedAt).ToListAsync();
    }

    public async Task<List<MediaAsset>> AssetsAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryDbContext>()
            .Assets.AsNoTracking().Include(a => a.Versions).Include(a => a.UnitLinks).ToListAsync();
    }

    public async Task<IntentState?> IntentStateAsync(Guid targetId)
    {
        await using var scope = host.CreateAsyncScope();
        var intent = await scope.ServiceProvider.GetRequiredService<IAcquisitionQuery>().GetByTargetAsync(targetId);
        return intent?.State;
    }

    /// <summary>How many outbox rows of one event type exist, published or not (the relay never deletes).</summary>
    public async Task<int> OutboxCountAsync(string eventType)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Outbox.CountAsync(m => m.EventType == eventType);
    }

    /// <summary>How many outbox rows are still waiting to be delivered.</summary>
    public async Task<int> UnpublishedOutboxCountAsync()
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Outbox.CountAsync(m => !m.Published);
    }

    /// <summary>The queued commands of one type, whatever state they are in.</summary>
    public async Task<List<QueuedCommand>> CommandsAsync(string commandType)
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationsDbContext>()
            .Commands.AsNoTracking().Where(c => c.CommandType == commandType)
            .OrderBy(c => c.QueuedAt).ToListAsync();
    }
}
