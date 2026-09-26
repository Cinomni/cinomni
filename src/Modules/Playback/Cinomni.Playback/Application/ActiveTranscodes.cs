using Cinomni.Playback.Diagnostics;
using Cinomni.Playback.Encoding;
using Microsoft.Extensions.Logging;

namespace Cinomni.Playback.Application;

/// <summary>Whether a session may start a transcode, and if not, whose limit it hit.</summary>
public enum TranscodeAdmission
{
    Admitted = 1,

    /// <summary>The node is already running <see cref="PlaybackOptions.MaxConcurrentTranscodes"/>.</summary>
    NodeFull = 2,

    /// <summary>The account already holds <see cref="PlaybackOptions.MaxTranscodesPerAccount"/>.</summary>
    AccountFull = 3,
}

/// <summary>How a transcode process ended on its own: its exit code and the end of its stderr.</summary>
public sealed record TranscodeExit(Guid SessionId, int ExitCode, string Diagnostics);

/// <summary>
/// The transcodes running on this node — one per session, from the moment a slot is admitted until
/// the session ends, goes silent, or the host stops. It is the one place that holds the FFmpeg
/// processes, so it is also the one place that can stop them.
/// <para>
/// An entry is a <b>stream</b>, not only a process: it keeps its slot after FFmpeg finishes the file,
/// because the converted copy stays on disk while the viewer is still watching it. That makes
/// <see cref="PlaybackOptions.MaxConcurrentTranscodes"/> a bound on CPU and on disk at once.
/// </para>
/// <para>
/// In memory on purpose: a process does not survive the process that started it, and the sweep
/// recovers whatever a previous run left on disk (<see cref="TranscodeSweep"/>). Admission happens
/// under one lock, so two requests racing for the last slot cannot both get it.
/// </para>
/// </summary>
public sealed class ActiveTranscodes(
    PlaybackOptions options,
    TranscodeWorkspace workspace,
    TimeProvider clock,
    ILogger<ActiveTranscodes> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>Set once the host starts stopping: from then on nothing new is admitted or attached.</summary>
    private bool _stopping;

    /// <summary>
    /// Sessions being closed right now: their entry is gone, their row not yet saved. The sweep leaves
    /// them to the close in hand, which knows why the session is ending — reconciling them instead would
    /// record them as recovered.
    /// </summary>
    private readonly HashSet<Guid> _closing = [];

    /// <summary>
    /// When this registry came up — in practice, when the host started. A session started before this
    /// instant cannot have a process here, which is how the sweep tells a previous run's leftovers
    /// from this run's.
    /// </summary>
    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Claims a slot for <paramref name="sessionId"/> before any process exists, so the check and the
    /// claim are one step. A refused session holds nothing.
    /// </summary>
    public TranscodeAdmission TryAdmit(Guid sessionId, Guid userId)
    {
        lock (_gate)
        {
            // A host on its way down is full: a process started now would outlive the StopAllAsync that
            // already ran.
            if (_stopping || _entries.Count >= options.MaxConcurrentTranscodes)
            {
                return TranscodeAdmission.NodeFull;
            }

            if (_entries.Values.Count(entry => entry.UserId == userId) >= options.MaxTranscodesPerAccount)
            {
                return TranscodeAdmission.AccountFull;
            }

            var now = clock.GetUtcNow();
            _entries[sessionId] = new Entry(userId, now, now);
            return TranscodeAdmission.Admitted;
        }
    }

    /// <summary>
    /// Hands the admitted session its running process. False when the slot is already gone — ended
    /// while FFmpeg was starting — and then the caller still owns the process and must stop it.
    /// </summary>
    public bool Attach(Guid sessionId, IRunningTranscode process)
    {
        lock (_gate)
        {
            if (_stopping || !_entries.TryGetValue(sessionId, out var entry) || entry.Process is not null)
            {
                return false;
            }

            entry.Process = process;
            entry.LastSeen = clock.GetUtcNow();
            return true;
        }
    }

    /// <summary>
    /// Takes back a stream a previous run left complete on disk: no process — FFmpeg had already
    /// finished — but a viewer may still be reading it, so it holds a slot, goes idle and reaches its
    /// lifetime (counted from <paramref name="startedAt"/>, when it was first admitted) like any other.
    /// </summary>
    public void Adopt(Guid sessionId, Guid userId, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (_entries.TryAdd(sessionId, new Entry(userId, startedAt, clock.GetUtcNow()) { ExitRecorded = true }))
            {
                PlaybackMetrics.RecordAdopted(sessionId);
            }
        }
    }

    /// <summary>Whether the session has a stream here, or is being closed by somebody right now.</summary>
    public bool IsTracked(Guid sessionId)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(sessionId) || _closing.Contains(sessionId);
        }
    }

    /// <summary>
    /// Ends a session's transcode on behalf of a close, holding the session back from the sweep until
    /// <paramref name="record"/> — which saves why it ended — has run.
    /// </summary>
    public async Task EndForCloseAsync(Guid sessionId, Func<Task> record)
    {
        lock (_gate)
        {
            _closing.Add(sessionId);
        }

        try
        {
            await EndAsync(sessionId);
            await record();
        }
        finally
        {
            lock (_gate)
            {
                _closing.Remove(sessionId);
            }
        }
    }

    /// <summary>Someone is still watching: a progress report or a segment request for this session.</summary>
    public void Touch(Guid sessionId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                entry.LastSeen = clock.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// Ends a session's transcode: frees its slot, stops its process and reclaims its output
    /// directory. Idempotent, safe for a session that never transcoded, and never throws — it runs on
    /// every way a session can end, and the request that triggered it may already be gone, which is
    /// why it takes no cancellation token.
    /// </summary>
    public async Task EndAsync(Guid sessionId)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.Remove(sessionId, out entry);
        }

        await StopAndReclaimAsync(sessionId, entry);
    }

    /// <summary>The sessions nothing has asked for within <see cref="PlaybackOptions.TranscodeIdleTimeout"/>.</summary>
    public IReadOnlyList<Guid> Idle()
    {
        var cutoff = IdleCutoff();
        lock (_gate)
        {
            return [.. _entries.Where(pair => pair.Value.LastSeen < cutoff).Select(pair => pair.Key)];
        }
    }

    /// <summary>The streams that have held their slot for <see cref="PlaybackOptions.MaxTranscodeLifetime"/>.</summary>
    public IReadOnlyList<Guid> Expired()
    {
        var cutoff = clock.GetUtcNow() - options.MaxTranscodeLifetime;
        lock (_gate)
        {
            return [.. _entries.Where(pair => pair.Value.AdmittedAt < cutoff).Select(pair => pair.Key)];
        }
    }

    /// <summary>
    /// Ends a transcode only if it is <b>still</b> idle, deciding under the same lock that takes it
    /// out: a list from <see cref="Idle"/> is already stale by the time anyone acts on it, and a viewer
    /// who came back in between must keep their stream.
    /// </summary>
    /// <returns><c>true</c> when it was idle and has been ended.</returns>
    public async Task<bool> EndIfIdleAsync(Guid sessionId)
    {
        var cutoff = IdleCutoff();
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out entry) || entry.LastSeen >= cutoff)
            {
                return false;
            }

            _entries.Remove(sessionId);
        }

        await StopAndReclaimAsync(sessionId, entry);
        return true;
    }

    /// <summary>
    /// Processes that have ended on their own and whose outcome has not been recorded yet. Reading
    /// them changes nothing; <see cref="ExitRecorded"/> does, once the outcome is safely persisted.
    /// </summary>
    public IReadOnlyList<TranscodeExit> PendingExits()
    {
        lock (_gate)
        {
            var exits = new List<TranscodeExit>();
            foreach (var (sessionId, entry) in _entries)
            {
                if (!entry.ExitRecorded && entry.Process is { HasExited: true, ExitCode: { } code } process)
                {
                    exits.Add(new TranscodeExit(sessionId, code, process.Diagnostics));
                }
            }

            return exits;
        }
    }

    public void ExitRecorded(Guid sessionId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entry))
            {
                entry.ExitRecorded = true;
            }
        }
    }

    /// <summary>
    /// Stops every process on the way down. Output directories stay: the sessions are still open in
    /// the database, and the next run's sweep decides whether each is adopted or reclaimed. Safe to
    /// repeat — a request that slipped in while the host was stopping is stopped by the next call.
    /// </summary>
    public async Task StopAllAsync()
    {
        List<(Guid SessionId, IRunningTranscode Process)> processes;
        lock (_gate)
        {
            _stopping = true;
            processes = [.. _entries
                .Where(pair => pair.Value.Process is not null)
                .Select(pair => (pair.Key, pair.Value.Process!))];
            _entries.Clear();
        }

        // One at a time and each on its own: a process that cannot be stopped must not leave the rest running.
        foreach (var (sessionId, process) in processes)
        {
            PlaybackMetrics.RecordStopped(sessionId);
            await StopQuietlyAsync(sessionId, process);
        }

        if (processes.Count > 0)
        {
            logger.LogInformation("Stopped {Count} transcodes on shutdown.", processes.Count);
        }
    }

    private DateTimeOffset IdleCutoff() => clock.GetUtcNow() - options.TranscodeIdleTimeout;

    private async Task StopAndReclaimAsync(Guid sessionId, Entry? entry)
    {
        // The gauge follows the registry: a stream stops counting when it leaves it, not when its
        // session row happens to reach a terminal state.
        PlaybackMetrics.RecordStopped(sessionId);
        if (entry?.Process is { } process)
        {
            await StopQuietlyAsync(sessionId, process);
        }

        // After the process, never before: FFmpeg would recreate what was removed on its next segment.
        workspace.Remove(sessionId);
    }

    /// <summary>
    /// Stopping is best-effort by nature — the slot is already free by the time it runs — so a process
    /// that refuses to go is logged and left to the operating system rather than thrown at a caller who
    /// can do nothing more about it.
    /// </summary>
    private async Task StopQuietlyAsync(Guid sessionId, IRunningTranscode process)
    {
        try
        {
            await process.DisposeAsync();
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Could not stop the transcode of session {SessionId}.", sessionId);
        }
    }

    private sealed class Entry(Guid userId, DateTimeOffset admittedAt, DateTimeOffset lastSeen)
    {
        public Guid UserId { get; } = userId;

        public DateTimeOffset AdmittedAt { get; } = admittedAt;

        public DateTimeOffset LastSeen { get; set; } = lastSeen;

        public IRunningTranscode? Process { get; set; }

        public bool ExitRecorded { get; set; }
    }
}
