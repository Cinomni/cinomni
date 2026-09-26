using Cinomni.Kernel.Messaging;

namespace Cinomni.Downloads.Contracts;

/// <summary>Stable registered names of the Downloads integration events.</summary>
public static class DownloadEventNames
{
    public const string DownloadStarted = "downloads.started";
    public const string MetadataReady = "downloads.metadata-ready";
    public const string DownloadCompleted = "downloads.completed";
    public const string DownloadFailed = "downloads.failed";
    public const string DownloadNotStarted = "downloads.not-started";
    public const string DownloadStateSaved = "downloads.state-saved";
    public const string TunnelEgressLost = "downloads.tunnel-egress-lost";
    public const string TunnelEgressRestored = "downloads.tunnel-egress-restored";
}

/// <summary>
/// A download began transferring for an acquisition attempt. Carries the acquisition
/// correlation ids (<see cref="IntentId"/>/<see cref="AttemptId"/>, echoed from
/// <c>DownloadQueued</c>) so Acquisition can advance the matching intent. Ids travel as raw
/// <see cref="Guid"/>/<see cref="string"/> for wire stability.
/// <para>
/// One event is published <b>per waiting attempt</b>. That is normally exactly one, and the
/// idempotency key therefore names the attempt as well as the task: when two goals selected the same
/// release, both share a single task, and a key naming only the task would deduplicate the second
/// goal's feedback away and strand it in <c>Downloading</c> forever.
/// </para>
/// </summary>
public sealed record DownloadStarted(
    Guid DownloadTaskId,
    Guid IntentId,
    Guid AttemptId,
    string InfoHash) : DomainEvent
{
    public override string IdempotencyKey => $"download-started:{DownloadTaskId}:{AttemptId}";
}

/// <summary>A magnet resolved its metadata: the file layout is now known.</summary>
public sealed record MetadataReady(Guid DownloadTaskId, string InfoHash, string Name) : DomainEvent
{
    public override string IdempotencyKey => $"download-metadata:{DownloadTaskId}";
}

/// <summary>
/// One file inside a completed torrent, as Downloads knows it. <see cref="RelativePath"/> is the
/// path recorded by the engine, relative to the torrent root — combine it with the event's
/// <c>ContentPath</c> to reach the file on disk.
/// </summary>
public sealed record DownloadedFile(int Index, string RelativePath, long Size);

/// <summary>
/// The download completed and its content is on disk at <see cref="ContentPath"/> (the staging
/// area). Consumed by Import (to land the files) and Acquisition (to advance the goal).
/// <see cref="WorkId"/>/<see cref="TargetId"/> are echoed from <c>DownloadQueued</c> (opaque
/// correlation) so Import can tie the asset to its work/target without reaching across a boundary.
/// </summary>
/// <param name="UnitIds">
/// The catalog units this download was meant to satisfy, echoed from <c>DownloadQueued</c>.
/// Trailing optional: the payload is persisted as jsonb, so an in-flight 1.x row deserializes with
/// it null.
/// </param>
/// <param name="Files">
/// The torrent's file layout, which Downloads has always persisted but never published.
/// <c>ContentPath</c> alone is a directory for a multi-file torrent, so a consumer that must map
/// files to units needs this. Trailing optional for the same wire-stability reason.
/// </param>
public sealed record DownloadCompleted(
    Guid DownloadTaskId,
    Guid IntentId,
    Guid AttemptId,
    Guid WorkId,
    Guid TargetId,
    string InfoHash,
    string ContentPath,
    IReadOnlyList<Guid>? UnitIds = null,
    IReadOnlyList<DownloadedFile>? Files = null) : DomainEvent
{
    // Attempt-scoped for the same reason as DownloadStarted: one shared task can have two waiting
    // attempts, and a task-only key would silently drop the second one's completion.
    public override string IdempotencyKey => $"download-completed:{DownloadTaskId}:{AttemptId}";
}

/// <summary>
/// The download failed. Consumed by Acquisition, which returns the goal to searching
/// (case #6) rather than dropping it. Recoverable engine warnings do not raise this — only a fatal
/// task error does.
/// </summary>
public sealed record DownloadFailed(
    Guid DownloadTaskId,
    Guid IntentId,
    Guid AttemptId,
    string Reason) : DomainEvent
{
    public override string IdempotencyKey => $"download-failed:{DownloadTaskId}:{AttemptId}";
}

/// <summary>
/// A release handed off by Acquisition never became a download: every attempt to give it to the engine
/// failed — a link the indexer no longer serves, a torrent the engine will not accept, an engine that
/// stayed out of reach. No task exists, so <see cref="DownloadFailed"/> cannot say it; without this the
/// goal would wait in <c>Downloading</c> for a task that is never coming. Consumed by Acquisition,
/// which treats it as a failed download of that attempt.
/// </summary>
/// <param name="Reason">A fixed sentence, never the underlying error: that can quote the indexer's link.</param>
public sealed record DownloadNotStarted(Guid IntentId, Guid AttemptId, string Reason) : DomainEvent
{
    public override string IdempotencyKey => $"download-not-started:{AttemptId}";
}

/// <summary>
/// A resume-data checkpoint (<c>.fastresume</c>) was persisted, so the download survives a
/// restart without a full recheck (case #14). Keyed by checkpoint sequence.
/// </summary>
public sealed record DownloadStateSaved(Guid DownloadTaskId, int CheckpointSeq) : DomainEvent
{
    public override string IdempotencyKey => $"download-state-saved:{DownloadTaskId}:{CheckpointSeq}";
}

/// <summary>
/// Torrent traffic is no longer verifiably leaving through the tunnel the installation opted into.
/// Published once per transition, not once per observation: the watch job runs on a short cadence and
/// an event per poll would be a notification storm for one outage.
/// <para>
/// <paramref name="Reason"/> is the machine-readable cause, carried rather than rendered so the
/// interface can translate it and so the fact survives in the outbox as something more useful than
/// "the VPN broke". It is one shared vocabulary: what the sidecar observed
/// (<c>tunnel-device-missing</c>, <c>tunnel-device-has-no-address</c>,
/// <c>default-route-not-via-tunnel</c>, <c>egress-identity-not-the-tunnel</c>,
/// <c>ipv6-egress-not-the-tunnel</c>, <c>no-egress-route</c>, <c>tunnel-guard-disabled</c>,
/// <c>tunnel-guard-observation-failed</c>) or what Downloads concluded (<c>sidecar-unreachable</c>,
/// <c>tunnel-observation-stale</c>, <c>tunnel-policy-divergent</c>, <c>tunnel-device-divergent</c>).
/// </para>
/// </summary>
/// <param name="Sequence">
/// Which loss this is, counted from the first one this installation ever saw. It is what makes the
/// idempotency key stable across redeliveries of one transition and distinct between two outages —
/// a key naming only the task set would deduplicate a second outage away.
/// </param>
/// <param name="HeldTaskCount">How many in-flight downloads were put on hold by this transition.</param>
/// <param name="Policy">
/// The loss policy in force, as its serialized name. Present because the same event means different
/// things under <c>Block</c> and <c>Ignore</c>, and a consumer must not have to guess which was set.
/// </param>
public sealed record TunnelEgressLost(
    int Sequence,
    string Reason,
    int HeldTaskCount,
    string Policy) : DomainEvent
{
    public override string IdempotencyKey => $"tunnel-egress-lost:{Sequence}";
}

/// <summary>
/// Torrent traffic is verifiably inside the tunnel again, and every download held for that reason has
/// been released. The counterpart of <see cref="TunnelEgressLost"/> and keyed by the same sequence, so
/// one outage produces exactly one loss and one restoration however often either is redelivered.
/// </summary>
/// <param name="ReleasedTaskCount">How many held downloads this transition returned to downloading.</param>
public sealed record TunnelEgressRestored(
    int Sequence,
    int ReleasedTaskCount) : DomainEvent
{
    public override string IdempotencyKey => $"tunnel-egress-restored:{Sequence}";
}
