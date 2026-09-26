using System.Globalization;
using Cinomni.Discovery.Contracts;
using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Diagnostics;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Application;

/// <summary>
/// Drives the download task lifecycle against the sidecar. Every engine call (an out-of-process
/// side effect) happens <b>before</b> the unit of work; the aggregate change and its integration
/// events are then written together in one transaction. Transitions fed by the status
/// stream are monotone, so the at-least-once pump can re-apply a snapshot harmlessly.
/// </summary>
public sealed class DownloadService(
    DownloadsDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ITorrentEngine engine,
    SidecarOptions options,
    TunnelOptions tunnel,
    TransferOptions transfers,
    TunnelWatchService watch,
    ILogger<DownloadService> logger,
    IReleaseFileSource? releaseFiles = null)
{
    /// <summary>States a task can still be observed/checkpointed in (everything but the terminals).</summary>
    private static readonly DownloadState[] MonitorableStates =
    [
        DownloadState.Queued, DownloadState.ResolvingMetadata, DownloadState.Checking,
        DownloadState.Downloading, DownloadState.Completed, DownloadState.Seeding,
    ];

    /// <summary>
    /// States a restart has to re-establish with the engine: everything the sidecar was still working
    /// on when the process went away. A <see cref="DownloadState.Completed"/> or
    /// <see cref="DownloadState.Seeding"/> task is deliberately excluded — its content is already
    /// handed to Import and re-adding it would only restore seeding, which is a policy nicety and not
    /// a recovery obligation.
    /// </summary>
    private static readonly DownloadState[] RecoverableStates =
    [
        DownloadState.Queued, DownloadState.ResolvingMetadata, DownloadState.Checking,
        DownloadState.Downloading, DownloadState.Paused,
    ];

    /// <summary>States in which a task owns its torrent at the engine: every one but the terminals.</summary>
    private static readonly DownloadState[] EngineOwnedStates =
    [
        DownloadState.Queued, DownloadState.ResolvingMetadata, DownloadState.Checking,
        DownloadState.Downloading, DownloadState.Paused, DownloadState.Completed, DownloadState.Seeding,
    ];

    /// <summary>
    /// ⇠ DownloadQueued: adds the release to the engine and persists a Queued task. Idempotent per
    /// attempt, and — the season-pack case — it refuses to create a <b>second task for an info-hash
    /// already in flight</b>, attaching the new attempt and its units to the existing task instead.
    /// </summary>
    /// <remarks>
    /// Without that guard the collision is unrecoverable rather than merely wasteful: the status
    /// stream subscribes per info-hash and <see cref="ApplyStatusAsync"/> looks a task up by it, so
    /// exactly one of the two tasks is ever advanced and the other sits in <c>Downloading</c>
    /// forever, with no timeout and no failure, holding its acquisition goal open with it.
    /// </remarks>
    public async Task AddDownloadAsync(
        Guid attemptId,
        Guid intentId,
        Guid workId,
        Guid targetId,
        string releaseGuid,
        string downloadUrl,
        IReadOnlyList<Guid>? unitIds = null,
        CancellationToken cancellationToken = default)
    {
        // Claims are the dedup key; the task's own attempt_id is checked too so a task written before
        // claims existed (and somehow not backfilled) can still never be duplicated.
        if (await dbContext.Claims.AnyAsync(c => c.AttemptId == attemptId, cancellationToken)
            || await dbContext.Tasks.AnyAsync(t => t.AttemptId == attemptId, cancellationToken))
        {
            return; // this attempt is already waiting on a download
        }

        // Nothing new is handed to an engine whose traffic is not going where it should. The refusal is
        // deliberately an exception and not a silent return: the queued command retries with backoff,
        // so a short outage costs a delay and nothing else, and a long one leaves a failed command
        // naming the cause instead of a download that quietly never happened. Returning success here
        // would drop the acquisition on the floor with no record at all.
        await RefuseWhileEgressIsHeldAsync(cancellationToken);

        // Every download gets a folder of its own, named after the attempt it serves. They all used to
        // share the staging root, so two releases with the same name wrote into one folder, and what a
        // failed one left behind could be imported as the other. The attempt, not a fresh id, because
        // this command is retried: a retry after the engine already took the torrent must name the same
        // folder the torrent is writing to, or the task would record an empty one.
        var savePath = $"{options.StagingPath.TrimEnd('/')}/{attemptId}";
        // Out-of-process side effects first, outside the transaction. Adding a torrent the engine
        // already holds is idempotent there and returns the same info-hash, which is exactly what
        // lets us detect the collision.
        //
        // No resume data, and not by omission: a checkpoint belongs to a task, and this call is what
        // creates the task. Re-adding an existing one from its blob is <see cref="RecoverAsync"/>'s
        // job, and it is the only place the persisted checkpoint is meaningful — inside a running
        // process the engine already holds the torrent and would ignore it.
        var added = await engine.AddAsync(
            await AddRequestForAsync(downloadUrl, savePath, resumeData: null, cancellationToken), cancellationToken);

        var inFlight = await FindInFlightByInfoHashAsync(added.InfoHash, cancellationToken);
        if (inFlight is not null)
        {
            await AttachToExistingAsync(inFlight, attemptId, intentId, targetId, unitIds, cancellationToken);
            return;
        }

        // The engine may still hold this torrent for an earlier task that finished and is seeding: adding
        // it again returned that torrent, which keeps writing where it always did. The new task follows
        // it there — its own fresh folder would stay empty and the import would find nothing.
        if ((await LoadByInfoHashAsync(added.InfoHash, cancellationToken))
                .FirstOrDefault(t => EngineOwnedStates.Contains(t.State)) is { } held)
        {
            savePath = held.SavePath;
        }

        var files = added.Name.Length > 0
            ? await engine.ListFilesAsync(added.InfoHash, cancellationToken)
            : [];

        var now = DateTimeOffset.UtcNow;
        var task = DownloadTask.Create(
            intentId, attemptId, workId, targetId, releaseGuid, downloadUrl, savePath, transfers.DefaultSeedingPolicy, now,
            unitIds);
        task.OnAdded(added.InfoHash, added.Name, now);
        if (files.Count > 0)
        {
            task.SetFiles(files.Select(f => (f.Index, f.Path, f.Size, f.Priority)));
        }

        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.Tasks.Add(task); // new root → cascade inserts the history, files, units, claim
            await dbContext.SaveChangesAsync(token);
            if (added.Name.Length > 0)
            {
                await eventBus.PublishAsync(new MetadataReady(task.Id, added.InfoHash, task.Name), token);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// ⇠ an <see cref="Messaging.AddDownloadCommand"/> that spent its last attempt: tells Acquisition the
    /// release never became a download, so the goal can move on instead of waiting in Downloading for a
    /// task that is never coming. Nothing is said when a task exists for the attempt after all — an add
    /// that landed but failed to report back — because that task reports for itself.
    /// </summary>
    public async Task ReportNotStartedAsync(Guid attemptId, Guid intentId, CancellationToken cancellationToken = default)
    {
        if (await dbContext.Claims.AnyAsync(c => c.AttemptId == attemptId, cancellationToken)
            || await dbContext.Tasks.AnyAsync(t => t.AttemptId == attemptId, cancellationToken))
        {
            return;
        }

        await unitOfWork.ExecuteAsync(
            async token => await eventBus.PublishAsync(
                new DownloadNotStarted(intentId, attemptId, "The release could not be handed to the download engine."),
                token),
            cancellationToken);
    }

    /// <summary>
    /// Stops an add before it reaches the engine while this installation is holding its downloads.
    /// <para>
    /// Without it the call still fails — under <c>Block</c> the sidecar is not even running — but it
    /// fails as a transport error after a deadline, once per retry, against a process that cannot
    /// help. Reading the state row costs one indexed lookup and turns that into a refusal that says
    /// what is wrong, which is what an operator reads off the failed command afterwards.
    /// </para>
    /// </summary>
    /// <exception cref="NetworkHoldException">Downloads are held because egress is not verified.</exception>
    private async Task RefuseWhileEgressIsHeldAsync(CancellationToken cancellationToken)
    {
        if (await ReadEgressHoldAsync(cancellationToken) is { } reason)
        {
            throw NetworkHoldException.ForNewDownload(reason);
        }
    }

    /// <summary>
    /// The reason this installation is currently holding its downloads, or null when it is not. Null
    /// is also the answer with no tunnel configured: there is then no hold, no policy and no row to
    /// read, which is the ordinary state of an installation that never opted in.
    /// </summary>
    private async Task<string?> ReadEgressHoldAsync(CancellationToken cancellationToken)
    {
        if (!tunnel.IsConfigured)
        {
            return null; // no tunnel: there is no hold and no row to read
        }

        var state = await dbContext.TunnelState
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == TunnelStateRecord.SingletonId, cancellationToken);

        return state is { Holding: true } ? state.Reason : null;
    }

    /// <summary>
    /// Why startup recovery must not put this installation's transfers back on the network, or null
    /// when it may. Stricter than <see cref="ReadEgressHoldAsync"/> on purpose.
    /// <para>
    /// The absence of a hold is not a verification. Recovery runs before this process has observed
    /// anything, so the row it reads was written by a process that no longer exists — or is the
    /// initial row, whose whole point is that "nothing has been checked yet" must never render as a
    /// verification. An installation that crashed while healthy and whose tunnel then failed to come
    /// up would otherwise have every checkpointed torrent handed back with no egress guarantee at all,
    /// which is precisely what the guard exists to prevent.
    /// </para>
    /// <para>
    /// So the answer has to be positive and fresh: verified, not holding, and observed recently enough
    /// to stand for the present. <see cref="TunnelWatchService"/> is asked for one observation
    /// immediately before this pass (see <c>RecoverDownloadsAsync</c>), so in the ordinary case the
    /// verdict read here is this process's own.
    /// </para>
    /// </summary>
    private async Task<string?> ReadRecoveryRefusalAsync(CancellationToken cancellationToken)
    {
        if (!tunnel.GuardActs)
        {
            return null; // no tunnel, or a policy that deliberately does not act
        }

        var state = await dbContext.TunnelState
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == TunnelStateRecord.SingletonId, cancellationToken);

        if (state is null)
        {
            return TunnelObservationReasons.NotYetObserved;
        }

        if (state.Holding || !state.Verified)
        {
            return state.Reason;
        }

        if (state.ObservedAt is not { } observedAt)
        {
            return TunnelObservationReasons.NotYetObserved;
        }

        return DateTimeOffset.UtcNow - observedAt > tunnel.MaxObservationAge
            ? TunnelObservationReasons.ObservationStale
            : null;
    }

    /// <summary>Records a second attempt (and any extra units) against the task already downloading it.</summary>
    private async Task AttachToExistingAsync(
        DownloadTask task,
        Guid attemptId,
        Guid intentId,
        Guid targetId,
        IReadOnlyList<Guid>? unitIds,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var claim = task.Claim(intentId, attemptId, targetId, isOriginating: false, now);
        if (claim is null)
        {
            return; // already attached (a redelivered hand-off)
        }

        var newUnits = task.AddUnits(unitIds ?? []);

        logger.LogInformation(
            "Attempt {AttemptId} selected info-hash {InfoHash}, already in flight as task {TaskId}; " +
            "attaching it instead of creating a second task.",
            attemptId, task.InfoHash, task.Id);

        await unitOfWork.ExecuteAsync(async token =>
        {
            // Children reached through a tracked aggregate's navigations carry client-assigned keys,
            // so EF would mis-track them as Modified; add them to their sets explicitly.
            dbContext.Claims.Add(claim);
            dbContext.Units.AddRange(newUnits);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// The task currently working on an info-hash, if any. Ordered newest-first because a hash may
    /// legitimately appear on several historical tasks (a torrent re-added after an earlier one was
    /// removed) and an unordered <c>FirstOrDefault</c> over a non-unique index picks arbitrarily.
    /// <para>
    /// A task that already finished is deliberately <b>not</b> a convergence point: its feedback has
    /// been published once, and Import's <c>process-completed-download:{downloadTaskId}</c> key is
    /// spent, so re-announcing it would strand the newcomer in <c>Importing</c> instead. The new
    /// attempt gets its own task, which reaches its terminal state from <c>Queued</c> because the
    /// engine reports the torrent finished right away (see <see cref="DownloadTask.MarkCompleted"/>).
    /// </para>
    /// </summary>
    private async Task<DownloadTask?> FindInFlightByInfoHashAsync(string infoHash, CancellationToken cancellationToken)
    {
        var candidates = await LoadByInfoHashAsync(infoHash, cancellationToken);
        return candidates.FirstOrDefault(t => t.IsInFlight);
    }

    private async Task<List<DownloadTask>> LoadByInfoHashAsync(string infoHash, CancellationToken cancellationToken) =>
        await dbContext.Tasks
            .Include(t => t.Files)
            .Include(t => t.History)
            .Include(t => t.Units)
            .Include(t => t.Claims)
            .Where(t => t.InfoHash == infoHash)
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Applies one status snapshot from the stream: refreshes the live figures, advances the state
    /// machine, and emits the matching integration event. Monotone and idempotent.
    /// </summary>
    public async Task ApplyStatusAsync(string infoHash, TorrentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        // Newest first, preferring one still in flight: the index is not unique, so a hash re-added
        // after an earlier task for it finished would otherwise resolve arbitrarily.
        var candidates = await LoadByInfoHashAsync(infoHash, cancellationToken);
        var task = candidates.FirstOrDefault(t => t.IsInFlight) ?? candidates.FirstOrDefault();
        if (task is null)
        {
            return;
        }

        // A magnet's layout only becomes known once its metadata resolves: fetch it outside the tx.
        // Keyed on the layout, not on the name. The engines report no name before the metadata, but a
        // name recorded before this rule existed is the magnet's dn=, not the folder the torrent
        // writes, and Import would be sent to scan a directory that never appears. Until the layout is
        // known the name is provisional, and the one that comes with the layout replaces it.
        // Only before completion: after it the content path is fixed, and a listing that failed would
        // otherwise cost the whole snapshot — progress, the finish, the end of seeding — every time.
        var metadataJustResolved = snapshot.Name.Length > 0 && task.Files.Count == 0 && task.ContentPath is null;
        var files = metadataJustResolved
            ? await ListFilesOrNothingAsync(infoHash, cancellationToken)
            : [];

        var now = DateTimeOffset.UtcNow;
        var historyBefore = task.History.Count;
        var filesBefore = task.Files.Count;
        var wasFailed = task.State is DownloadState.Error;
        var events = new List<IDomainEvent>();

        // The engine's own queue is waiting; a manual pause at the engine under a running row is not,
        // and is left to stall and fail rather than to hold the goal for ever.
        task.TrackProgress(snapshot.Progress, snapshot.IsQueued, now);
        task.UpdateProgress(
            snapshot.Progress, snapshot.DownloadRate, snapshot.UploadRate, snapshot.NumPeers,
            snapshot.NumSeeds, snapshot.AllTimeUpload, snapshot.AllTimeDownload, snapshot.SeedingSeconds, now);

        // Aggregate throughput and stall detection. The info hash is a lookup key inside the metrics
        // type and is never tagged, published or exported — it identifies exactly what is being
        // downloaded, which is the most sensitive value this module holds.
        DownloadsMetrics.RecordSnapshot(
            infoHash,
            snapshot.DownloadRate,
            snapshot.UploadRate,
            snapshot.NumPeers + snapshot.NumSeeds,
            snapshot.IsFinished,
            now);

        // A known name with no layout yet (the engine listed nothing this time) is left for the next
        // snapshot to retry, and announced only once there is something new to say.
        if (metadataJustResolved && (files.Count > 0 || task.Name.Length == 0))
        {
            task.OnAdded(infoHash, snapshot.Name, now);
            if (files.Count > 0)
            {
                task.SetFiles(files.Select(f => (f.Index, f.Path, f.Size, f.Priority)));
            }

            events.Add(new MetadataReady(task.Id, infoHash, task.Name));
        }

        // A held task absorbs figures but no transitions. The snapshot is describing a session this
        // installation deliberately stopped, so an engine error raised by the tunnel going away would
        // otherwise become DownloadFailed — which sends the acquisition goal back to searching for
        // the length of the outage and loses the transfer's progress. A hold is not a failure.
        if (!task.IsNetworkHeld)
        {
            ApplyTransition(task, snapshot, now, events);
            FailIfStalled(task, now, events);
        }

        var newHistory = task.History.Skip(historyBefore).ToList();
        var newFiles = task.Files.Skip(filesBefore).ToList();
        await unitOfWork.ExecuteAsync(async token =>
        {
            // Children reached through a tracked aggregate's navigations must be added to their sets
            // explicitly (client-assigned keys make EF mis-track them as Modified otherwise).
            dbContext.History.AddRange(newHistory);
            dbContext.Files.AddRange(newFiles);
            await dbContext.SaveChangesAsync(token);
            foreach (var domainEvent in events)
            {
                await eventBus.PublishAsync(domainEvent, token);
            }
        }, cancellationToken);

        if (!wasFailed && task.State is DownloadState.Error)
        {
            // A failed task no longer owns its torrent: nothing streams, checkpoints, holds or stops
            // an Error row. Left in the engine, a stalled one kept announcing and could later download
            // and seed with no owner, invisible and outside the tunnel guard.
            await DiscardForeignAsync(infoHash, cancellationToken);
        }

        if (task.ShouldStopSeeding())
        {
            await StopSeedingAsync(task, cancellationToken);
        }
    }

    /// <exception cref="DownloadStateConflictException">The task is past the point a pause applies to.</exception>
    public Task<bool> PauseAsync(DownloadTaskId id, CancellationToken cancellationToken = default) =>
        ControlAsync(
            id, task => task.EnsureCanPause(), engine.PauseAsync, (task, now) => task.Pause(now), cancellationToken);

    /// <summary>
    /// Resumes a held task. <paramref name="force"/> is the override the <c>PauseAndAlert</c> policy
    /// allows and <c>Block</c> refuses: under Block the whole point is that no manual action puts
    /// traffic back on an unverified path, so the flag is ignored there and the refusal stands.
    /// </summary>
    /// <exception cref="NetworkHoldException">The task is network-held and the override does not apply.</exception>
    /// <exception cref="DownloadStateConflictException">The task is not one a resume applies to.</exception>
    public async Task<bool> ResumeAsync(DownloadTaskId id, bool force = false, CancellationToken cancellationToken = default)
    {
        var task = await LoadWithHistory(id, cancellationToken);
        if (task?.InfoHash is not { } infoHash)
        {
            return false;
        }

        // The refusal is checked before the engine is touched, not after. Going the other way would
        // tell the sidecar to resume the torrent and only then decide it was not allowed to.
        //
        // With no tunnel configured there is no policy to enforce and the override is an ordinary
        // operator decision: the hold on the row is the residue of a guard this installation no
        // longer has. The watch releases it within a poll anyway — this is what keeps the minute in
        // between from answering 409 on behalf of a tunnel that is gone.
        var mayOverride = force
            && (!tunnel.IsConfigured || tunnel.LossPolicy is not TunnelLossPolicy.Block);
        task.EnsureCanResume(mayOverride);

        // The override has two halves. The row's hold is lifted below; the engine's session-wide hold
        // is lifted here, or the resumed torrent would move nothing inside a held session.
        if (task.IsNetworkHeld && tunnel.IsConfigured)
        {
            await engine.OverrideTunnelHoldAsync(cancellationToken);
        }

        await engine.ResumeAsync(infoHash, cancellationToken);
        var historyBefore = task.History.Count;
        task.Resume(DateTimeOffset.UtcNow, mayOverride);
        await PersistHistory(task, historyBefore, cancellationToken);
        return true;
    }

    /// <summary>
    /// Removes every task a cancelled goal claimed that is not already gone, each exactly as an operator's
    /// remove would. One task at a time, so a failure part-way leaves the rest for the retry rather than
    /// rolling back torrents the engine has already dropped.
    /// </summary>
    public async Task RemoveForGoalAsync(Guid intentId, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        var taskIds = await dbContext.Tasks
            .AsNoTracking()
            .Where(t => t.State != DownloadState.Removed && t.Claims.Any(c => c.IntentId == intentId))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        foreach (var taskId in taskIds)
        {
            await RemoveAsync(new DownloadTaskId(taskId), deleteFiles, cancellationToken);
        }
    }

    public async Task<bool> RemoveAsync(DownloadTaskId id, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        var task = await dbContext.Tasks
            .Include(t => t.History)
            .Include(t => t.Claims)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id.Value, cancellationToken);
        if (task?.InfoHash is not { } infoHash)
        {
            return false;
        }

        await engine.RemoveAsync(infoHash, deleteFiles, cancellationToken);

        // No further status snapshot will arrive for this torrent, so the in-flight view has to be told.
        // Left in, it would hold the active count up and keep adding its last transfer rate to the
        // aggregate for the life of the process — a gauge that never comes back down.
        DownloadsMetrics.Forget(infoHash);

        // A task removed before it finished is a failed download to every goal waiting on it. Nothing
        // else will ever report on it — no snapshot streams for a torrent the engine dropped — so
        // without this each goal sat in Downloading for ever with nothing behind it. A finished task
        // (Completed, Seeding) already reported its completion and an Error one its failure: removing
        // those tells nobody anything new.
        var events = task.State is DownloadState.Completed or DownloadState.Seeding or DownloadState.Error or DownloadState.Removed
            ? []
            : PerClaim(task, c => new DownloadFailed(task.Id, c.IntentId, c.AttemptId, RemovedBeforeFinishing)).ToList();

        var historyBefore = task.History.Count;
        task.Remove(DateTimeOffset.UtcNow);
        var newHistory = task.History.Skip(historyBefore).ToList();
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
            foreach (var domainEvent in events)
            {
                await eventBus.PublishAsync(domainEvent, token);
            }
        }, cancellationToken);
        return true;
    }

    private const string RemovedBeforeFinishing = "The download was removed from the download client before it finished.";

    public async Task<bool> SetFilePrioritiesAsync(
        DownloadTaskId id,
        IReadOnlyDictionary<int, FilePriorityLevel> priorities,
        CancellationToken cancellationToken = default)
    {
        var task = await dbContext.Tasks.Include(t => t.Files).FirstOrDefaultAsync(t => t.Id == id.Value, cancellationToken);
        if (task?.InfoHash is not { } infoHash)
        {
            return false;
        }

        await engine.SetFilePrioritiesAsync(infoHash, priorities, cancellationToken);
        task.SetFilePriorities(priorities, DateTimeOffset.UtcNow);
        // Only existing file rows change (Modified) — no new children, so no explicit Add is needed.
        await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
        return true;
    }

    /// <summary>
    /// Startup recovery: hands every transfer this installation was still working on back to the
    /// engine, <b>from its persisted resume-data checkpoint</b>, and reconciles the answer into the
    /// task. Returns how many tasks were re-established.
    /// <para>
    /// This is what makes the checkpoint written by <see cref="SaveCheckpointsAsync"/> worth writing.
    /// Nothing else in the product reads <see cref="DownloadTask.ResumeData"/>: without this pass a
    /// restart leaves the sidecar holding nothing, the status stream produces no snapshot for a task
    /// that no longer exists there, and a download sits in <c>Downloading</c> for ever with no
    /// timeout and no failure — taking its acquisition goal with it.
    /// </para>
    /// <para>
    /// Idempotent by construction. Adding a torrent the engine already holds returns the same
    /// info-hash and changes nothing there, no task or claim is created, and
    /// <see cref="ApplyStatusAsync"/> is monotone — so running this twice, or running it against an
    /// engine that never went away, is a no-op beyond one history line per task.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Recovery never fights the tunnel guard for a task. It refuses to run at all unless egress is
    /// positively and freshly verified, and skips a task the guard is individually holding: handing
    /// such a transfer back to the engine is precisely what the guard exists to prevent, and the
    /// guard's own release path is what re-establishes it. One gap remains on purpose — under
    /// <c>PauseAndAlert</c> a startup observation that fails without crossing the hold threshold
    /// leaves the transfers stopped until the next start — because the alternative is putting traffic
    /// on an unverified path to close it.
    /// </remarks>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (await ReadRecoveryRefusalAsync(cancellationToken) is { } refusal)
        {
            logger.LogWarning(
                "Download recovery did not run: torrent egress is not verified ({Reason}), so no transfer was "
                + "handed back to the engine. The guard re-establishes them when egress verifies again.",
                refusal);
            return 0;
        }

        // Ids first, then one task at a time: the engine calls are out-of-process and a long list must
        // not be held in a single materialized graph while they run.
        var ids = await dbContext.Tasks
            .AsNoTracking()
            .Where(t => RecoverableStates.Contains(t.State) && t.NetworkHoldSince == null)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return 0;
        }

        var recovered = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await RecoverOneAsync(id, cancellationToken))
            {
                recovered += 1;
            }
        }

        logger.LogInformation(
            "Download recovery re-established {RecoveredCount} of {CandidateCount} in-flight transfer(s).",
            recovered,
            ids.Count);
        return recovered;
    }

    /// <summary>
    /// Re-establishes the named tasks with the engine from their checkpoints, and returns how many
    /// were. This is the tunnel guard's release path (⇠ <see cref="TunnelWatchService"/>), and it
    /// re-adds rather than resumes for one reason: the commonest cause of a hold is the sidecar
    /// process going away, which clears every torrent it held. Resuming an info-hash nobody holds
    /// succeeds and transfers nothing, leaving the row saying <c>Downloading</c> with a valid
    /// checkpoint while the household waits for a file that is not coming. Re-adding is idempotent at
    /// the engine, so it is the right call whether the sidecar restarted or not.
    /// </summary>
    public async Task<int> ReEstablishAsync(IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken = default)
    {
        var reEstablished = 0;
        foreach (var id in taskIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await RecoverOneAsync(id, cancellationToken))
            {
                reEstablished += 1;
            }
        }

        return reEstablished;
    }

    /// <summary>
    /// ⇠ the status pump, when a transfer this backend is still running is one the engine no longer
    /// knows — the sidecar restarted (an out-of-memory kill, a recreated container) underneath a
    /// backend that did not. Without this nothing re-adds it until the backend itself restarts: the
    /// stream only ever answers "unknown", checkpoints skip it, and the row says Downloading for good.
    /// <para>
    /// Held to the same rule as startup recovery: nothing goes back on the network unless egress is
    /// positively and freshly verified, and a task the guard is holding is left to the guard's own
    /// release. The engine is asked once more before anything is re-added, so a stream that merely
    /// failed — a refused slot, a dropped connection — is never mistaken for a lost torrent.
    /// </para>
    /// </summary>
    /// <returns>Whether a transfer was re-established.</returns>
    public async Task<bool> RecoverLostAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        if (await ReadRecoveryRefusalAsync(cancellationToken) is not null)
        {
            return false;
        }

        if (await engine.GetStatusAsync(infoHash, cancellationToken) is not null)
        {
            return false; // the engine still holds it; the stream failed for some other reason
        }

        // And asked afresh of the sidecar that is running now. The verdict on record may be minutes old
        // and describe the process that just died, which is the very event that brings this path here.
        if (!await watch.VerifiesNowAsync(cancellationToken))
        {
            return false;
        }

        var ids = await dbContext.Tasks
            .AsNoTracking()
            .Where(t => t.InfoHash == infoHash && RecoverableStates.Contains(t.State) && t.NetworkHoldSince == null)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        var recovered = false;
        foreach (var id in ids)
        {
            recovered |= await RecoverOneAsync(id, cancellationToken);
        }

        if (recovered)
        {
            logger.LogInformation("Re-established a transfer the engine no longer held.");
        }

        return recovered;
    }

    /// <summary>
    /// Re-establishes one task. Returns false when the engine could not be reached for it — a sidecar
    /// that is simply absent must leave the task exactly as it was, not fail the caller: the next
    /// restart tries again, and nothing has been lost in the meantime.
    /// </summary>
    private async Task<bool> RecoverOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks
            .Include(t => t.History)
            .Include(t => t.Files)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null || !RecoverableStates.Contains(task.State) || task.IsNetworkHeld)
        {
            return false; // moved on while we were working through the list
        }

        TorrentAdded added;
        try
        {
            // Out-of-process and outside the transaction, exactly like an ordinary add. The checkpoint
            // rides along: with it the engine re-checks what is already on disk instead of fetching it
            // again, and without it the transfer restarts — correct either way, just slower.
            added = await engine.AddAsync(
                await AddRequestForAsync(task.DownloadUrl, task.SavePath, task.ResumeData, cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No identifier in the message: an info-hash names what a household is transferring.
            logger.LogWarning(ex, "Download recovery could not re-add one transfer to the engine; leaving it untouched.");
            return false;
        }

        if (!IsTheSameTorrent(task, added))
        {
            // The answer is checked before it is believed, because on this path the torrent's identity
            // comes only from the persisted checkpoint: the adapter does not send the download URL once
            // resume data exists, and the blob carries the swarm's own info dictionary. Anyone able to
            // write downloads.download_tasks.resume_data — a restored or tampered backup, an
            // operator-supplied dump — could otherwise re-point a live task, the claims waiting on it
            // and its acquisition goal at different content, and steer Import's scan root with it.
            // An empty answer is refused for the same reason: it would blank the key every control call
            // and every status snapshot looks this task up by.
            //
            // No identifier in the message: an info-hash names what a household is transferring.
            logger.LogWarning(
                "Download recovery refused to re-bind task {TaskId}: the engine answered with a different "
                + "torrent than the one the task is bound to. The task was left untouched.",
                task.Id);
            await DiscardForeignAsync(added.InfoHash, cancellationToken);
            return false;
        }

        // A task an operator had paused stays paused. The add starts the torrent, so it is stopped
        // again immediately — the alternative is not re-adding it at all, which would leave a paused
        // download that no later resume could ever start, because the engine has never heard of it.
        //
        // Every other task is told to run, because an add does not guarantee it. A sidecar that never
        // restarted answers a re-add with the handle it already holds, still paused by the hold that
        // just ended; and a checkpoint written during that hold carries the paused flag into a fresh
        // sidecar too. Either way the row would say Downloading while nothing moved.
        if (task.State is DownloadState.Paused)
        {
            await SilenceAsync(added.InfoHash, cancellationToken);
        }
        else
        {
            await WakeAsync(added.InfoHash, cancellationToken);
        }

        // A magnet's layout becomes known when its metadata resolves, and for a task re-added from a
        // checkpoint that moment can be this very call: the blob carries the torrent's own info
        // dictionary, so the engine answers with the name straight away. Capturing it here is the only
        // chance to announce it on this path; ApplyStatusAsync would otherwise pick the layout up on
        // the next snapshot. Keyed on the layout and not on the name, like there: a name recorded
        // before the metadata arrived is not the torrent's.
        var metadataJustResolved = added.Name.Length > 0 && task.Files.Count == 0 && task.ContentPath is null;
        var files = metadataJustResolved
            ? await ListFilesOrNothingAsync(added.InfoHash, cancellationToken)
            : [];

        var historyBefore = task.History.Count;
        var filesBefore = task.Files.Count;
        var nameBefore = task.Name;
        task.MarkRecovered(added.InfoHash, added.Name, added.Resumed, DateTimeOffset.UtcNow);
        // Announced only when there is something new to say, as on the status path: a restart that
        // learns nothing must not repeat the announcement.
        metadataJustResolved &= files.Count > 0 || task.Name != nameBefore;
        if (files.Count > 0 && filesBefore == 0)
        {
            task.SetFiles(files.Select(f => (f.Index, f.Path, f.Size, f.Priority)));
        }

        // The history line, the layout and the announcement commit together: an announced layout that
        // was not written is a signal about rows nobody can read.
        var newHistory = task.History.Skip(historyBefore).ToList();
        var newFiles = task.Files.Skip(filesBefore).ToList();
        try
        {
            await unitOfWork.ExecuteAsync(async token =>
            {
                dbContext.History.AddRange(newHistory);
                dbContext.Files.AddRange(newFiles);
                await dbContext.SaveChangesAsync(token);
                if (metadataJustResolved)
                {
                    await eventBus.PublishAsync(new MetadataReady(task.Id, added.InfoHash, task.Name), token);
                }
            }, cancellationToken);
        }
        finally
        {
            // Whether or not the record above committed. A pause, hold or removal that landed while this
            // ran also wrote a history line, and this commit can lose that race on the line's sequence —
            // which is exactly the case where the engine most needs to be brought back in line.
            await EnforceRecordedStateAsync(task.Id, added.InfoHash, CancellationToken.None);
        }

        await ReconcileAsync(added.InfoHash, cancellationToken);
        return true;
    }

    /// <summary>
    /// Makes the engine agree with the row as it stands <b>now</b>, after the re-add and the start. This
    /// path runs alongside the tunnel guard, an operator's pause and an operator's removal, and each of
    /// those commits to the row before it acts on the engine — so one landing between this method's
    /// read and its engine calls would otherwise be undone by them: a held or paused task running, or
    /// a removed one re-added with nothing left to ever stop it. Reading after acting closes every
    /// such interleaving, because whatever committed first is what is read here.
    /// </summary>
    private async Task EnforceRecordedStateAsync(Guid id, string infoHash, CancellationToken cancellationToken)
    {
        var current = await dbContext.Tasks
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.State, t.NetworkHoldSince })
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null || current.State is DownloadState.Removed)
        {
            await DiscardForeignAsync(infoHash, cancellationToken);
        }
        else if (current.State is DownloadState.Paused || current.NetworkHoldSince is not null)
        {
            await SilenceAsync(infoHash, cancellationToken);
        }
    }

    /// <summary>
    /// Takes a torrent this call just put into the engine back out, unless a live task owns it. Best
    /// effort, and never deleting files: what is on disk may be somebody else's, and a torrent left
    /// running with no task behind it is the thing being prevented — nothing would ever stream,
    /// checkpoint or stop it.
    /// </summary>
    private async Task DiscardForeignAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (infoHash.Length == 0)
        {
            return;
        }

        // Read untracked: this context is still tracking the task the caller loaded, and identity
        // resolution would answer with that copy and its state from before the row moved on. A task
        // still seeding owns its torrent as much as one still downloading does.
        var owned = await dbContext.Tasks
            .AsNoTracking()
            .AnyAsync(
                t => t.InfoHash == infoHash && EngineOwnedStates.Contains(t.State),
                cancellationToken);
        if (owned)
        {
            return;
        }

        try
        {
            await engine.RemoveAsync(infoHash, deleteFiles: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The engine could not be told to drop a torrent no task owns any more.");
        }
    }

    /// <summary>
    /// Whether the engine's answer describes the torrent this task is bound to. A task that never
    /// recorded an info-hash (added while the engine was answering, and never reconciled) accepts the
    /// first non-empty answer, which is the only way it can ever acquire one.
    /// </summary>
    private static bool IsTheSameTorrent(DownloadTask task, TorrentAdded added) =>
        added.InfoHash.Length > 0
        && (task.InfoHash is not { Length: > 0 } bound
            || string.Equals(added.InfoHash, bound, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The torrent's file layout, or nothing when the engine could not produce it. Not knowing the
    /// layout is a smaller loss than not re-establishing the transfer, so it never fails recovery.
    /// </summary>
    private async Task<IReadOnlyList<TorrentFileInfo>> ListFilesOrNothingAsync(
        string infoHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await engine.ListFilesAsync(infoHash, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Download recovery re-added a transfer but could not read its file layout.");
            return [];
        }
    }

    /// <summary>Stops a transfer at the engine, best effort — a failure here is not a recovery failure.</summary>
    private async Task SilenceAsync(string infoHash, CancellationToken cancellationToken)
    {
        try
        {
            await engine.PauseAsync(infoHash, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Download recovery could not stop a re-added transfer that was paused.");
        }
    }

    /// <summary>Starts a re-added transfer at the engine, best effort — like the stop, never a recovery failure.</summary>
    private async Task WakeAsync(string infoHash, CancellationToken cancellationToken)
    {
        try
        {
            await engine.ResumeAsync(infoHash, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Download recovery re-added a transfer but could not start it.");
        }
    }

    /// <summary>
    /// Folds the engine's first answer after a re-add into the task, so a torrent that finished while
    /// the process was away is recognised straight away rather than waiting for the status stream. A
    /// snapshot the engine cannot yet produce is not an error: the pump takes over from here.
    /// </summary>
    private async Task ReconcileAsync(string infoHash, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await engine.GetStatusAsync(infoHash, cancellationToken);
            if (snapshot is not null)
            {
                await ApplyStatusAsync(infoHash, snapshot, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Download recovery re-added a transfer but could not read its status back.");
        }
    }

    /// <summary>
    /// Periodic maintenance (scheduler-driven): for each active task, refresh its state from the
    /// engine (applying seeding-policy cutoffs) and persist a fresh resume-data checkpoint.
    /// </summary>
    public async Task SaveCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await dbContext.Tasks
            .Where(t => MonitorableStates.Contains(t.State) && t.InfoHash != null)
            .Select(t => new { t.Id, t.InfoHash, t.State })
            .ToListAsync(cancellationToken);

        var failed = 0;
        foreach (var entry in entries)
        {
            var infoHash = entry.InfoHash!;
            try
            {
                // The live pump owns transitions while a task is streaming; here we only reconcile
                // tasks whose stream has closed (Completed/Seeding) to enforce the seeding policy —
                // this avoids racing the pump on the same task's history rows.
                if (entry.State is DownloadState.Completed or DownloadState.Seeding)
                {
                    var snapshot = await engine.GetStatusAsync(infoHash, cancellationToken);
                    if (snapshot is null)
                    {
                        // Nothing left to checkpoint: the torrent is gone with the engine that held it.
                        await EndLostSeedingAsync(new DownloadTaskId(entry.Id), cancellationToken);
                        continue;
                    }

                    await ApplyStatusAsync(infoHash, snapshot, cancellationToken);
                }

                await CheckpointOneAsync(new DownloadTaskId(entry.Id), infoHash, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One task's failure is that task's, not the pass's: before, the first one aborted the
                // loop and every task after it went without a checkpoint — every time, when the cause
                // was a blob too large to transfer. What it left half-tracked must not ride along
                // into the next task's save either. No info-hash in the message: it names the content.
                failed++;
                dbContext.ChangeTracker.Clear();
                logger.LogWarning(ex, "Download task {TaskId} could not be checkpointed; the pass goes on.", entry.Id);
            }
        }

        if (failed > 0)
        {
            logger.LogWarning("The checkpoint pass finished with {Failed} of {Total} tasks not saved.", failed, entries.Count);
        }
    }

    private async Task CheckpointOneAsync(DownloadTaskId id, string infoHash, CancellationToken cancellationToken)
    {
        var resume = await engine.SaveResumeDataAsync(infoHash, cancellationToken);
        if (resume is null || resume.Length == 0)
        {
            return;
        }

        var task = await dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id.Value, cancellationToken);
        if (task is null)
        {
            return;
        }

        var seq = task.SaveCheckpoint(resume, DateTimeOffset.UtcNow);
        await unitOfWork.ExecuteAsync(async token =>
        {
            await dbContext.SaveChangesAsync(token);
            await eventBus.PublishAsync(new DownloadStateSaved(task.Id, seq), token);
        }, cancellationToken);
    }

    private async Task StopSeedingAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        if (task.InfoHash is not { } infoHash)
        {
            return;
        }

        await engine.RemoveAsync(infoHash, deleteFiles: false, cancellationToken);
        var historyBefore = task.History.Count;
        task.EndSeeding(DateTimeOffset.UtcNow);
        await PersistHistory(task, historyBefore, cancellationToken);
    }

    /// <summary>
    /// A finished task whose torrent the engine no longer holds is closed, not re-added: seeding is
    /// what the engine does with a torrent it still has, and a restart ends it. Re-adding a finished
    /// torrent would re-download whatever of it had since been deleted from staging.
    /// </summary>
    private async Task EndLostSeedingAsync(DownloadTaskId id, CancellationToken cancellationToken)
    {
        var task = await LoadWithHistory(id, cancellationToken);
        if (task is null)
        {
            return;
        }

        var historyBefore = task.History.Count;
        if (task.EndSeedingLost(DateTimeOffset.UtcNow))
        {
            await PersistHistory(task, historyBefore, cancellationToken);
        }
    }

    private async Task<bool> ControlAsync(
        DownloadTaskId id,
        Action<DownloadTask> precondition,
        Func<string, CancellationToken, Task> engineCall,
        Action<DownloadTask, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var task = await LoadWithHistory(id, cancellationToken);
        if (task?.InfoHash is not { } infoHash)
        {
            return false;
        }

        // Before the engine is touched: a transition the row then refused would have acted on the
        // torrent anyway — pausing a seeding one — and answered the operator with a fault.
        precondition(task);
        await engineCall(infoHash, cancellationToken);
        var historyBefore = task.History.Count;
        transition(task, DateTimeOffset.UtcNow);
        await PersistHistory(task, historyBefore, cancellationToken);
        return true;
    }

    /// <summary>
    /// What to hand the engine for a release link. A checkpoint needs nothing fetched. A link that
    /// belongs to an indexer that signs in is fetched by Discovery, as the member, and handed over as
    /// the file itself (or the magnet it answered with); every other link is the engine's to fetch
    /// anonymously, as it always was. Called at the moment of adding, never ahead of it: a private
    /// tracker may count the fetch as a download by the member.
    /// </summary>
    private async Task<TorrentAddRequest> AddRequestForAsync(
        string downloadUrl, string savePath, byte[]? resumeData, CancellationToken cancellationToken)
    {
        var plain = new TorrentAddRequest(downloadUrl, savePath, resumeData, options.StagingPath);
        if (resumeData is { Length: > 0 } || releaseFiles is null
            || !downloadUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return plain;
        }

        // A failure here throws, and the add is retried by its command like any engine failure.
        var file = await releaseFiles.FetchAsync(downloadUrl, cancellationToken);
        return file.Outcome switch
        {
            ReleaseFileOutcome.TorrentFile => plain with { TorrentFile = file.TorrentFile },
            ReleaseFileOutcome.Magnet => plain with { DownloadUrl = file.Magnet! },
            _ => plain,
        };
    }

    private Task<DownloadTask?> LoadWithHistory(DownloadTaskId id, CancellationToken cancellationToken) =>
        dbContext.Tasks.Include(t => t.History).FirstOrDefaultAsync(t => t.Id == id.Value, cancellationToken);

    private async Task PersistHistory(DownloadTask task, int historyBefore, CancellationToken cancellationToken)
    {
        var newHistory = task.History.Skip(historyBefore).ToList();
        await unitOfWork.ExecuteAsync(async token =>
        {
            dbContext.History.AddRange(newHistory);
            await dbContext.SaveChangesAsync(token);
        }, cancellationToken);
    }

    private static void ApplyTransition(DownloadTask task, TorrentSnapshot snapshot, DateTimeOffset now, List<IDomainEvent> events)
    {
        if (snapshot.Error is { } error)
        {
            if (task.Fail(error, now))
            {
                // Counted by outcome only. The engine's error text quotes tracker responses and file
                // paths, and the info hash names the content — neither becomes a label.
                DownloadsMetrics.RecordFinished(task.InfoHash ?? string.Empty, CinomniTelemetry.Outcomes.Failed);
                events.AddRange(PerClaim(task, c => new DownloadFailed(task.Id, c.IntentId, c.AttemptId, error)));
            }

            return;
        }

        if (snapshot.IsFinished && task.Name.Length == 0 && task.ContentPath is null)
        {
            // The swarm's name sanitised away to nothing (".", "...", " . "), so there is no folder of
            // this download's own: its content path would be the staging root, a directory of every
            // download, and Import would take another one's files for this work. A failure, so the
            // goal moves on and this release is not picked again.
            if (task.Fail(UnusableNameError, now))
            {
                DownloadsMetrics.RecordFinished(task.InfoHash ?? string.Empty, CinomniTelemetry.Outcomes.Failed);
                events.AddRange(PerClaim(task, c => new DownloadFailed(task.Id, c.IntentId, c.AttemptId, UnusableNameError)));
            }

            return;
        }

        if (snapshot.IsFinished)
        {
            var contentPath = ContentPathOf(task);
            if (task.MarkCompleted(contentPath, now))
            {
                DownloadsMetrics.RecordFinished(task.InfoHash ?? string.Empty, CinomniTelemetry.Outcomes.Completed);
                var units = UnitsOf(task);
                var layout = LayoutOf(task);
                events.AddRange(PerClaim(task, c => new DownloadCompleted(
                    task.Id, c.IntentId, c.AttemptId, task.WorkId, c.TargetId, task.InfoHash ?? string.Empty,
                    contentPath, UnitIds: units, Files: layout)));
            }

            if (IsSeedingState(snapshot.State))
            {
                task.MarkSeeding(now);
            }

            return;
        }

        switch (snapshot.State)
        {
            case "downloading_metadata":
                task.MarkResolvingMetadata(now);
                break;
            case "checking_files" or "checking_resume_data" or "allocating":
                task.MarkChecking(now);
                break;
            case "downloading":
                if (task.MarkDownloading(now))
                {
                    events.AddRange(PerClaim(task, c => new DownloadStarted(
                        task.Id, c.IntentId, c.AttemptId, task.InfoHash ?? string.Empty)));
                }

                break;
        }
    }

    /// <summary>
    /// Fails a transfer that has been trying and failing to move for longer than the stall timeout, so
    /// its goal can move on to another release. A release nobody shares otherwise sat in
    /// <c>Downloading</c> for ever, and its acquisition goal waited with it.
    /// </summary>
    private void FailIfStalled(DownloadTask task, DateTimeOffset now, List<IDomainEvent> events)
    {
        if (!task.HasStalled(transfers.StallTimeout, now))
        {
            return;
        }

        // Neutral on purpose: a dead swarm, a magnet whose metadata never came and a network that is
        // down all look alike from here. The counts are what an operator reads the cause from.
        var reason = string.Create(
            CultureInfo.InvariantCulture,
            $"No progress in {FormatTimeout(transfers.StallTimeout)} ({task.NumSeeds} seeds, {task.NumPeers} peers at the last look).");
        if (task.Fail(reason, now))
        {
            DownloadsMetrics.RecordFinished(task.InfoHash ?? string.Empty, CinomniTelemetry.Outcomes.Failed);
            events.AddRange(PerClaim(task, c => new DownloadFailed(task.Id, c.IntentId, c.AttemptId, reason)));
        }
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalHours:0.#} hours")
            : string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalMinutes:0.#} minutes");

    /// <summary>
    /// One event per waiting attempt — normally exactly one, so the movie path is unchanged. A task
    /// two goals share must report to both, or the second goal never learns its download finished.
    /// A task written before claims existed has none, so it falls back to its own attempt.
    /// </summary>
    private static IEnumerable<IDomainEvent> PerClaim(
        DownloadTask task,
        Func<DownloadTaskClaim, IDomainEvent> build)
    {
        if (task.Claims.Count == 0)
        {
            return [build(new DownloadTaskClaim
            {
                DownloadTaskId = task.Id,
                AttemptId = task.AttemptId,
                IntentId = task.IntentId,
                TargetId = task.TargetId,
                IsOriginating = true,
            })];
        }

        return task.Claims.OrderBy(c => c.CreatedAt).ThenBy(c => c.AttemptId).Select(build).ToList();
    }

    /// <summary>
    /// The catalog units the download served. Falls back to the work id for a task written before
    /// units were persisted — which is the movie unit, so the payload is identical either way.
    /// </summary>
    private static IReadOnlyList<Guid> UnitsOf(DownloadTask task) =>
        task.Units.Count > 0 ? task.Units.Select(u => u.UnitId).ToList() : [task.WorkId];

    private static bool IsSeedingState(string state) => state is "seeding";

    /// <summary>
    /// Projects the persisted <c>downloads.torrent_files</c> rows into the published language, in
    /// engine file order. Empty when the layout is unknown (a magnet whose metadata never resolved).
    /// </summary>
    private static IReadOnlyList<DownloadedFile> LayoutOf(DownloadTask task) =>
        task.Files
            .OrderBy(f => f.Index)
            .Select(f => new DownloadedFile(f.Index, f.Path, f.Size))
            .ToList();

    private const string UnusableNameError = "The torrent's name leaves it no folder of its own to import from.";

    private static string ContentPathOf(DownloadTask task) => $"{task.SavePath.TrimEnd('/')}/{task.Name}";
}
