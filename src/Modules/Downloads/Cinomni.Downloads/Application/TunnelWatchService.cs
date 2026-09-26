using Cinomni.Downloads.Contracts;
using Cinomni.Downloads.Diagnostics;
using Cinomni.Downloads.Engine;
using Cinomni.Downloads.Persistence;
using Cinomni.Kernel.Messaging;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinomni.Downloads.Application;

/// <summary>
/// Turns "where is the traffic actually going" into a durable decision about the downloads.
/// <para>
/// The observation is an out-of-process call and happens <b>before</b> the unit of work; the state
/// row, the held tasks and the integration event are then written together in one transaction, so an
/// installation can never end up with downloads held and no record of why, or an event announcing a
/// hold that never happened.
/// </para>
/// <para>
/// It acts on <b>transitions</b>, not on observations. The job runs on a short cadence and an event
/// per poll would be a notification every thirty seconds for one outage; the hysteresis counters on
/// the state row are what turn a stream of observations into the two facts an operator cares about.
/// </para>
/// </summary>
public sealed class TunnelWatchService(
    DownloadsDbContext dbContext,
    IUnitOfWork unitOfWork,
    IEventBus eventBus,
    ITorrentEngine engine,
    IServiceScopeFactory scopes,
    TunnelOptions options,
    ILogger<TunnelWatchService> logger)
{
    /// <summary>
    /// One cycle: observe, fold the observation into the streaks, and act if a threshold was crossed.
    /// Safe to run again immediately — a cycle that changes nothing writes only the streak counters.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (!options.IsConfigured)
        {
            // The release path runs before the short-circuit, not after it. An installation that
            // removes the tunnel while an outage is in progress — the documented way back to a direct
            // connection — would otherwise leave every held download stopped for good: nothing else in
            // the system clears a network hold, and a held task still counts as in flight, so the next
            // acquisition attaches to a task that can never run. Turning the guard off has to be able
            // to let go of what the guard was holding.
            await ReleaseAbandonedHoldAsync(now, cancellationToken);
            return;
        }

        // Outside the transaction: this is a gRPC call to another process and it can hang, fail or
        // answer that it does not implement the question.
        var observation = await ObserveAsync(now, cancellationToken);

        var state = await LoadOrCreateStateAsync(now, cancellationToken);
        state.Observe(observation.Verified, observation.Reason, DeviceOf(observation), observation.ObservedAt, now);

        var action = Decide(state);
        if (action is TunnelAction.None)
        {
            await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
            return;
        }

        if (action is TunnelAction.Hold)
        {
            await HoldAsync(state, observation, now, cancellationToken);
            return;
        }

        await ReleaseAsync(state, now, cancellationToken);
    }

    /// <summary>What a state row has just earned, given the configured policy and thresholds.</summary>
    private enum TunnelAction
    {
        None = 0,
        Hold = 1,
        Release = 2,
    }

    /// <summary>
    /// The policy applied to the streaks. Pure: it reads the row and the options and decides nothing
    /// about the world, which is what makes each mode testable on its own.
    /// </summary>
    private TunnelAction Decide(TunnelStateRecord state)
    {
        // Ignore observes and reports and never takes a hold. It is still worth observing: the
        // operator asked to be able to see this, they just did not ask Cinomni to act on it. What it
        // must still do is *lift* a hold this installation already has — an operator who weakens the
        // policy mid-outage (which is exactly what the drop drill's negative control asks for) would
        // otherwise strand every held download with no way back short of editing the database.
        if (options.LossPolicy is TunnelLossPolicy.Ignore)
        {
            return state.Holding ? TunnelAction.Release : TunnelAction.None;
        }

        if (!state.Holding && state.UnverifiedStreak >= EffectiveUnverifiedThreshold())
        {
            return TunnelAction.Hold;
        }

        if (state.Holding && state.VerifiedStreak >= options.VerifiedThreshold)
        {
            return TunnelAction.Release;
        }

        return TunnelAction.None;
    }

    /// <summary>
    /// How many failed observations it takes to hold, which is where <c>Block</c> and
    /// <c>PauseAndAlert</c> genuinely differ.
    /// <para>
    /// Under <c>Block</c> the first failed observation is enough. That is the fail-closed reading of
    /// "the VPN is unavailable": every observation that is not a verification — including one that
    /// could not be taken, because an unreachable sidecar is not evidence of safety — stops the
    /// traffic immediately, and the cost of being wrong is a pause that lifts on the next poll.
    /// Under <c>PauseAndAlert</c> the configured threshold applies, so a single blip is absorbed and
    /// only a sustained failure holds. That is the trade an operator makes when they choose it.
    /// </para>
    /// </summary>
    private int EffectiveUnverifiedThreshold() =>
        options.LossPolicy is TunnelLossPolicy.Block ? 1 : options.UnverifiedThreshold;

    /// <summary>
    /// Whether egress verifies <b>now</b>, asked of the engine that is running now and qualified by the
    /// same rules as every watch cycle. Records nothing: it is for a caller about to put a transfer
    /// back on the network outside a cycle — after a sidecar restart the verdict on record can describe
    /// the process that just died. True when the guard does not act, since there is nothing to verify.
    /// </summary>
    public async Task<bool> VerifiesNowAsync(CancellationToken cancellationToken = default) =>
        !options.GuardActs || (await ObserveAsync(DateTimeOffset.UtcNow, cancellationToken)).Verified;

    /// <summary>
    /// One observation that cannot fail. The engine contract already promises this, and this is the
    /// belt: an exception escaping here would abort the cycle <b>before</b> the streak was touched, so
    /// no hold would ever be declared and the surface would keep showing the last good verdict — an
    /// engine that cannot answer would present as an installation that is fine.
    /// </summary>
    private async Task<TunnelObservation> ObserveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return Qualify(await engine.ObserveTunnelAsync(cancellationToken), now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The egress observation could not be taken; treating it as unverified.");
            return TunnelObservation.Unreachable(options.Device, now);
        }
    }

    /// <summary>
    /// Second-guesses an answer that arrived intact, because two ways this guard fails leave the
    /// answer itself looking perfectly healthy.
    /// <para>
    /// The first is age. The sidecar reports when it looked, not when it was asked, so a guard thread
    /// that stopped observing keeps publishing its last verdict for as long as the process lives. An
    /// observation older than a few polls is not a fresh verification, it is a recording of one.
    /// </para>
    /// <para>
    /// The second is divergence. The two halves of this kill-switch are configured separately, and a
    /// sidecar enforcing a weaker policy — or watching a different interface — is one that will not
    /// stop its own traffic when this backend believes it has. That is on the wire in every
    /// observation and must be read rather than discarded.
    /// </para>
    /// </summary>
    private TunnelObservation Qualify(TunnelObservation observation, DateTimeOffset now)
    {
        if (!observation.Verified)
        {
            return observation; // already unverified: the observed cause is the more useful one
        }

        if (now - observation.ObservedAt > options.MaxObservationAge)
        {
            return Unverified(observation, TunnelObservationReasons.ObservationStale);
        }

        if (IsWeakerPolicy(observation.Policy))
        {
            return Unverified(observation, TunnelObservationReasons.PolicyDivergent);
        }

        return IsDifferentDevice(observation.TunnelDevice)
            ? Unverified(observation, TunnelObservationReasons.DeviceDivergent)
            : observation;
    }

    /// <summary>The same observation, told as the failure it actually is.</summary>
    private static TunnelObservation Unverified(TunnelObservation observation, string reason) =>
        observation with
        {
            TunnelUp = false,
            DefaultRouteViaTunnel = false,
            EgressIdentityMatches = false,
            Reason = reason,
        };

    /// <summary>
    /// Whether the sidecar reported enforcing something weaker than this installation configured. An
    /// unrecognised or absent value is not treated as divergence: it is a sidecar that does not report
    /// its policy, which the observation's own failure modes already cover.
    /// </summary>
    private bool IsWeakerPolicy(string reported) =>
        RankOf(reported) is { } rank && rank > (int)options.LossPolicy;

    private bool IsDifferentDevice(string reported) =>
        reported.Length > 0
        && options.Device.Length > 0
        && !string.Equals(reported, options.Device, StringComparison.Ordinal);

    /// <summary>
    /// The reported policy as its strength, or null when it is not one of the three. The numbers are
    /// <see cref="TunnelLossPolicy"/>'s own, which are ordered strongest to weakest by design, and the
    /// spellings are the ones the sidecar accepts for the single shared setting.
    /// </summary>
    private static int? RankOf(string policy) =>
        policy.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant() switch
        {
            "block" => (int)TunnelLossPolicy.Block,
            "pauseandalert" => (int)TunnelLossPolicy.PauseAndAlert,
            "ignore" => (int)TunnelLossPolicy.Ignore,
            _ => null,
        };

    private async Task HoldAsync(
        TunnelStateRecord state,
        TunnelObservation observation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sequence = state.BeginHold(now);
        if (sequence is not { } declared)
        {
            await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
            return;
        }

        // Untracked on purpose: the aggregate decides the transition and writes its own history line,
        // and the row itself is then updated under a predicate (see WriteAsync) rather than from this
        // snapshot. The snapshot is minutes old by database standards — an observation with a deadline
        // was taken before it — and another writer owns the same rows.
        var candidates = await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.History)
            .Where(t => InFlightStates.Contains(t.State) && t.NetworkHoldSince == null)
            .ToListAsync(cancellationToken);

        var pending = new List<PendingWrite>();
        foreach (var task in candidates)
        {
            var read = PendingWrite.Read(task);
            if (!task.HoldForNetwork(observation.Reason, now))
            {
                continue;
            }

            pending.Add(read);
        }

        var stopped = await WriteAsync(pending, cancellationToken, async (token, count) =>
        {
            logger.LogWarning(
                "Torrent egress is not verified ({Reason}); holding {HeldCount} download(s) under the {Policy} policy.",
                observation.Reason,
                count,
                options.LossPolicy);
            // An installation that opted into a tunnel and is now running held is the state that matters
            // most in this module, and until now it was only visible to somebody reading the interface.
            // The device name is not a tag: it is local network configuration.
            DownloadsMetrics.RecordTunnelTransition("held");
            await eventBus.PublishAsync(
                new TunnelEgressLost(declared, observation.Reason, count, options.LossPolicy.ToString()),
                token);
        });

        await StopAtTheEngineAsync(stopped, cancellationToken);
    }

    private async Task ReleaseAsync(TunnelStateRecord state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sequence = state.EndHold(now);
        if (sequence is not { } declared)
        {
            await unitOfWork.ExecuteAsync(dbContext.SaveChangesAsync, cancellationToken);
            return;
        }

        var candidates = await dbContext.Tasks
            .AsNoTracking()
            .Include(t => t.History)
            .Where(t => t.NetworkHoldSince != null)
            .ToListAsync(cancellationToken);

        var pending = new List<PendingWrite>();
        foreach (var task in candidates)
        {
            var read = PendingWrite.Read(task);
            if (!task.ReleaseNetworkHold(now))
            {
                continue;
            }

            pending.Add(read);
        }

        var resumed = await WriteAsync(pending, cancellationToken, async (token, count) =>
        {
            logger.LogInformation(
                "Torrent egress is verified again; released {ReleasedCount} held download(s).",
                count);
            DownloadsMetrics.RecordTunnelTransition("released");
            await eventBus.PublishAsync(new TunnelEgressRestored(declared, count), token);
        });

        await StartAtTheEngineAsync(resumed, cancellationToken);
    }

    /// <summary>
    /// One decided move: the task, the row as it was read, and the history the aggregate produced.
    /// The "as read" halves are what the write predicate re-asserts, so a row that changed underneath
    /// is recognised instead of overwritten.
    /// </summary>
    private sealed record PendingWrite(
        DownloadTask Task,
        DownloadState WasIn,
        bool WasHeld,
        int HistoryBefore)
    {
        /// <summary>
        /// Captures the row before the aggregate is asked to decide anything. Taken first because
        /// everything below compares the decision against the row as it was read, and the aggregate
        /// has overwritten that by the time it answers.
        /// </summary>
        public static PendingWrite Read(DownloadTask task) =>
            new(task, task.State, task.NetworkHoldSince is not null, task.History.Count);

        /// <summary>The history lines this decision added.</summary>
        public IEnumerable<DownloadHistoryRecord> NewHistory => Task.History.Skip(HistoryBefore);

        /// <summary>Whether the decision moved the task's state, which is what the engine must hear about.</summary>
        public bool StateChanged => Task.State != WasIn;
    }

    /// <summary>
    /// Commits the decided moves and publishes the transition, in one unit of work, and answers with
    /// the tasks the engine now has to be told about.
    /// <para>
    /// Each row is updated under a predicate that repeats what the snapshot assumed — the state it was
    /// read in, and whether it was already held. The watch is a second writer over rows the status
    /// pump owns, and a plain tracked update would carry a snapshot taken before a fifteen-second call
    /// timeout: a download that completed in that window would be silently reverted to <c>Paused</c>,
    /// its terminal state lost and its completion event re-emitted on release. A row that moved under
    /// us simply does not match, and is left to the next cycle.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<DownloadTask>> WriteAsync(
        IReadOnlyList<PendingWrite> pending,
        CancellationToken cancellationToken,
        Func<CancellationToken, int, Task> announce)
    {
        var toldToTheEngine = new List<DownloadTask>();
        var moved = 0;

        await unitOfWork.ExecuteAsync(async token =>
        {
            // The state row's own change goes first, so the statements below run against a context
            // with nothing pending: a set-based update does not see the change tracker.
            await dbContext.SaveChangesAsync(token);

            foreach (var entry in pending)
            {
                var task = entry.Task;
                var rows = dbContext.Tasks.Where(t => t.Id == task.Id && t.State == entry.WasIn);
                rows = entry.WasHeld
                    ? rows.Where(t => t.NetworkHoldSince != null)
                    : rows.Where(t => t.NetworkHoldSince == null);

                var updated = await rows.ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(t => t.State, task.State)
                        .SetProperty(t => t.NetworkHoldSince, task.NetworkHoldSince)
                        .SetProperty(t => t.NetworkHoldReason, task.NetworkHoldReason)
                        .SetProperty(t => t.LastProgressAt, task.LastProgressAt)
                        .SetProperty(t => t.UpdatedAt, task.UpdatedAt),
                    token);

                if (updated == 0)
                {
                    continue; // the row moved while the observation was being taken; not ours to write
                }

                moved += 1;
                dbContext.History.AddRange(entry.NewHistory);
                if (entry.StateChanged && task.InfoHash is { Length: > 0 })
                {
                    toldToTheEngine.Add(task);
                }
            }

            await dbContext.SaveChangesAsync(token);
            await announce(token, moved);
        }, cancellationToken);

        return toldToTheEngine;
    }

    /// <summary>
    /// Tells the engine to stop the transfers this cycle held. Best effort, and deliberately outside
    /// the unit of work: the durable decision is already committed and an engine that cannot be
    /// reached must not roll it back.
    /// <para>
    /// It matters because the sidecar's own session-wide pause is absent in precisely the cases where
    /// this backend is the only guard left — a sidecar too old to answer the question, a control
    /// credential that does not match, a tunnel device the sidecar was never given. Without this call
    /// the notification, the history line and the whole interface would say "held" while libtorrent
    /// kept transferring, which is worse than having no kill-switch at all.
    /// </para>
    /// </summary>
    private async Task StopAtTheEngineAsync(IReadOnlyList<DownloadTask> tasks, CancellationToken cancellationToken)
    {
        var failed = 0;
        Exception? last = null;
        foreach (var task in tasks)
        {
            try
            {
                await engine.PauseAsync(task.InfoHash!, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed += 1;
                last = ex;
            }
        }

        if (failed > 0)
        {
            // Counts only: an info-hash names what a household is transferring and does not belong in
            // a log an operator pastes into an issue.
            logger.LogWarning(
                last,
                "Could not stop {FailedCount} of {TotalCount} held transfer(s) at the engine; they are "
                + "recorded as held and may still be running.",
                failed,
                tasks.Count);
        }
    }

    /// <summary>
    /// The counterpart: the transfers this cycle released have to start again.
    /// <para>
    /// They are <b>re-established from their checkpoints</b>, not resumed. The commonest cause of a
    /// hold is <c>sidecar-unreachable</c>, and the commonest cause of that is the sidecar process
    /// restarting — which clears every torrent it held. A resume aimed at an info-hash nobody holds
    /// succeeds, transfers nothing, and leaves the row saying <c>Downloading</c> with progress and a
    /// valid checkpoint for ever: no timeout, no failure, and an interface telling the household the
    /// download is running. Re-adding is idempotent at the engine, so it is right either way.
    /// </para>
    /// <para>
    /// Outside the unit of work and best effort, like the stop: the durable release is already
    /// committed, and an engine that cannot be reached must not roll it back. What is left behind then
    /// is an unheld task the next start re-establishes, which is the ordinary recovery path.
    /// </para>
    /// </summary>
    private async Task StartAtTheEngineAsync(IReadOnlyList<DownloadTask> tasks, CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            // Its own scope, and not merely for tidiness: this cycle's context is still tracking the
            // aggregate graph the release just committed, and the re-establish loads the same rows
            // again to write its own history against them. Two live graphs of one task in one context
            // is an identity conflict, and the price of getting it wrong is the transfer silently not
            // coming back — which is the very failure this call exists to remove.
            await using var scope = scopes.CreateAsyncScope();
            var reEstablished = await scope.ServiceProvider
                .GetRequiredService<DownloadService>()
                .ReEstablishAsync([.. tasks.Select(task => task.Id)], cancellationToken);
            if (reEstablished < tasks.Count)
            {
                logger.LogWarning(
                    "Could not re-establish {FailedCount} of {TotalCount} released transfer(s) at the engine; "
                    + "they are recorded as running and are re-established on the next start.",
                    tasks.Count - reEstablished,
                    tasks.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not re-establish {TotalCount} released transfer(s) at the engine; they are recorded "
                + "as running and are re-established on the next start.",
                tasks.Count);
        }
    }

    /// <summary>
    /// Lets go of a hold this installation can no longer justify, because the tunnel it was watching
    /// is no longer configured. Nothing is created: an installation that never had a tunnel has no
    /// row, reads one indexed key per cycle and does nothing at all.
    /// </summary>
    private async Task ReleaseAbandonedHoldAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await dbContext.TunnelState
            .FirstOrDefaultAsync(x => x.Id == TunnelStateRecord.SingletonId, cancellationToken);
        if (state is not { Holding: true })
        {
            return;
        }

        logger.LogWarning(
            "The tunnel guard is no longer configured while downloads were held; releasing them. "
            + "Torrent traffic now leaves over this machine's own connection.");
        state.Observe(
            verified: false,
            TunnelObservationReasons.GuardRemoved,
            state.TunnelDevice,
            now,
            now);
        await ReleaseAsync(state, now, cancellationToken);
    }

    /// <summary>
    /// The state row, created on first use. Not seeded by the migration on purpose: a seeded row
    /// would claim an observation that never happened, and "not yet observed" is a real state an
    /// operator should be able to see on a freshly started installation.
    /// </summary>
    private async Task<TunnelStateRecord> LoadOrCreateStateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await dbContext.TunnelState
            .FirstOrDefaultAsync(x => x.Id == TunnelStateRecord.SingletonId, cancellationToken);
        if (state is not null)
        {
            return state;
        }

        state = TunnelStateRecord.Initial(now);
        dbContext.TunnelState.Add(state);
        return state;
    }

    /// <summary>
    /// Which device to record. The sidecar's own answer wins when it has one — it is the process that
    /// actually bound the socket — and the configured name is the fallback for an observation that
    /// could not be taken at all.
    /// </summary>
    private string DeviceOf(TunnelObservation observation) =>
        observation.TunnelDevice.Length > 0 ? observation.TunnelDevice : options.Device;

    /// <summary>The states a hold applies to: everything the sidecar is still working on.</summary>
    private static readonly DownloadState[] InFlightStates =
    [
        DownloadState.Queued, DownloadState.ResolvingMetadata,
        DownloadState.Checking, DownloadState.Downloading, DownloadState.Paused,
    ];
}
