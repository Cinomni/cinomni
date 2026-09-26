using Cinomni.Kernel.Identifiers;

namespace Cinomni.Downloads.Contracts;

/// <summary>Stable internal identity of a download task (UUIDv7).</summary>
public readonly record struct DownloadTaskId(Guid Value)
{
    public static DownloadTaskId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The lifecycle of a persisted download task (cases #9/#13/#14). Kept in the
/// database, not in memory, so it survives a restart and is reconciled against the sidecar's
/// resume data. A task is terminal only at <see cref="Removed"/>; <see cref="Error"/> is
/// recoverable (clear + retry) and <see cref="Paused"/> is a manual hold.
/// </summary>
public enum DownloadState
{
    Queued = 1,
    ResolvingMetadata = 2,
    Checking = 3,
    Downloading = 4,
    Completed = 5,
    Seeding = 6,
    Removed = 7,
    Paused = 8,
    Error = 9,
}

/// <summary>
/// Per-file download priority, mapped onto libtorrent's 0..7 scale: a file at
/// <see cref="Skip"/> is not downloaded (its pieces land in the part-file).
/// </summary>
public enum FilePriorityLevel
{
    Skip = 0,
    Normal = 1,
    High = 6,
    Top = 7,
}

/// <summary>
/// What Cinomni does when torrent traffic stops verifiably leaving through the tunnel the
/// installation opted into. Ordered from strongest to weakest, and the order is the point: anything
/// below <see cref="Block"/> is a conscious choice that is logged by name at startup and reported on
/// the operator's health surface, so a weakened installation can never look like a default one.
/// <para>
/// The policy only exists once a tunnel is configured. With no tunnel, Cinomni behaves exactly as it
/// always has and this value is never read.
/// </para>
/// </summary>
public enum TunnelLossPolicy
{
    /// <summary>
    /// Fail closed, and the default. Any observation that is not "verified inside the tunnel" —
    /// including one that could not be taken at all, because an unreachable sidecar is not evidence
    /// of safety — holds every in-flight download. A held download cannot be resumed by hand: the
    /// hold is released by the tunnel coming back and by nothing else.
    /// </summary>
    Block = 1,

    /// <summary>
    /// Stop and say so, but treat "I could not tell" as inconclusive rather than as loss: only a
    /// positively observed failure holds downloads, and an operator may override a hold knowingly.
    /// The override is recorded on the task's history, so the decision stays explainable.
    /// </summary>
    PauseAndAlert = 2,

    /// <summary>
    /// Observe and report, change nothing. Downloads keep running over whatever route exists, which
    /// may be the household's own connection. Chosen by an operator who wants the visibility without
    /// the enforcement; never chosen by default and never inferred from a misspelt setting.
    /// </summary>
    Ignore = 3,
}

/// <summary>
/// What the installation currently knows about its torrent egress path: the last observation, the
/// policy in force, and what that combination did to the downloads. Persisted rather than computed
/// on read, so an operator who arrives after an outage still sees why their downloads stopped.
/// </summary>
/// <param name="Configured">
/// Whether a tunnel is configured at all. False is the ordinary, supported state and means every
/// other field is inert — not a failure.
/// </param>
/// <param name="Verified">Whether traffic was last observed to be leaving inside the tunnel.</param>
/// <param name="Reason">
/// The machine-readable cause of the last observation. Never a rendered sentence: the interface
/// translates it, and the backend keeps the fact — which is why the vocabulary is enumerated in full
/// rather than by example, since a value the interface has never heard of reaches an operator raw.
/// <para>
/// Observed by the sidecar: <c>tunnel-egress-verified</c>, <c>tunnel-device-missing</c>,
/// <c>tunnel-device-has-no-address</c>, <c>default-route-not-via-tunnel</c>,
/// <c>egress-identity-not-the-tunnel</c>, <c>ipv6-egress-not-the-tunnel</c>, <c>no-egress-route</c>,
/// <c>tunnel-guard-disabled</c>, <c>tunnel-guard-observation-failed</c>.
/// </para>
/// <para>
/// Concluded by this module: <c>sidecar-unreachable</c>, <c>not-yet-observed</c>,
/// <c>tunnel-observation-stale</c>, <c>tunnel-policy-divergent</c>, <c>tunnel-device-divergent</c>,
/// <c>tunnel-guard-removed</c>.
/// </para>
/// </param>
/// <param name="TunnelDevice">The interface the sidecar was told to bind to, as it reported it.</param>
/// <param name="HeldTaskCount">
/// How many downloads are on hold right now because of this. It includes a download an operator had
/// already paused by hand: the hold goes on top of the manual pause and is what refuses to put it
/// back on an unverified path, so it is genuinely held even though nothing about its state changed.
/// </param>
/// <param name="TransitionSequence">How many losses this installation has seen. Correlates the events.</param>
/// <param name="ObservedAt">When the observation behind this state was taken.</param>
public sealed record TunnelEgressStatus(
    bool Configured,
    TunnelLossPolicy Policy,
    bool Verified,
    string Reason,
    string TunnelDevice,
    int HeldTaskCount,
    int TransitionSequence,
    DateTimeOffset? ObservedAt);

/// <summary>Projection of a download task's current state and live transfer figures.</summary>
/// <param name="NetworkHeld">
/// Whether this task is paused because the tunnel could not be verified, rather than because someone
/// paused it. Trailing optional for wire stability. It is deliberately carried beside
/// <see cref="DownloadState.Paused"/> instead of being a new state: the state name crosses the wire
/// and drives the interface's vocabulary, and a held download is a paused download whose reason is
/// recorded — which is what its history line already says.
/// </param>
/// <param name="IntentIds">
/// Every acquisition goal waiting on this task, each once: normally the one it was created for, more
/// when goals picked the same torrent (a season pack shared by a season and an episode inside it).
/// It is what lets an operator's view put a transfer under the title it serves instead of beside it.
/// Trailing optional for wire stability; absent reads as empty.
/// </param>
public sealed record DownloadTaskSummary(
    DownloadTaskId Id,
    string InfoHash,
    string Name,
    DownloadState State,
    double Progress,
    long DownloadRate,
    long UploadRate,
    int NumPeers,
    int NumSeeds,
    bool NetworkHeld = false,
    IReadOnlyList<Guid>? IntentIds = null)
{
    public IReadOnlyList<Guid> IntentIds { get; init; } = IntentIds ?? [];
}

/// <summary>
/// The wire shape of a download task: exactly what <c>GET /api/downloads/</c> returns, and exactly what
/// the realtime progress snapshot carries. It is published rather than hand-written at each site because
/// the two must be the same object to the browser — the live stream writes it straight into the cache
/// entry the REST call filled, and a field that drifted between them would surface as a download whose
/// figures change shape when the stream reconnects.
/// </summary>
public sealed record DownloadTaskWire(
    string Id,
    string InfoHash,
    string Name,
    string State,
    double Progress,
    long DownloadRate,
    long UploadRate,
    int NumPeers,
    int NumSeeds,
    bool NetworkHeld = false,
    IReadOnlyList<string>? IntentIds = null)
{
    public IReadOnlyList<string> IntentIds { get; init; } = IntentIds ?? [];

    public static DownloadTaskWire From(DownloadTaskSummary task) => new(
        task.Id.ToString(),
        task.InfoHash,
        task.Name,
        task.State.ToString(),
        task.Progress,
        task.DownloadRate,
        task.UploadRate,
        task.NumPeers,
        task.NumSeeds,
        task.NetworkHeld,
        task.IntentIds.Select(id => id.ToString()).ToList());
}

/// <summary>Projection of one file inside a torrent and its selected priority.</summary>
public sealed record DownloadFileSummary(
    int Index,
    string Path,
    long Size,
    FilePriorityLevel Priority);

/// <summary>One immutable line of the task's transition history (append-only, observability).</summary>
public sealed record DownloadHistoryEntry(
    int Seq,
    DownloadState From,
    DownloadState To,
    string Trigger,
    DateTimeOffset OccurredAt,
    string? Note);

/// <summary>The full view of a download task: its state, its files, and its transition history.</summary>
public sealed record DownloadTaskDetail(
    DownloadTaskSummary Task,
    IReadOnlyList<DownloadFileSummary> Files,
    IReadOnlyList<DownloadHistoryEntry> History);
