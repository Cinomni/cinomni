using Cinomni.Kernel.Identifiers;

namespace Cinomni.Import.Contracts;

/// <summary>Stable internal identity of an import job (UUIDv7).</summary>
public readonly record struct ImportJobId(Guid Value)
{
    public static ImportJobId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The lifecycle of an import job (cases #7/#8/#9/#15). A completed download is landed
/// into the library through a guarded, <b>recoverable</b> pipeline: it is matched to a unit, decided
/// against import specs, its files are operated (hardlink/copy/move/rename with verify + rollback),
/// probed with ffprobe, and finally registered as a media asset. <see cref="Registered"/>,
/// <see cref="Rejected"/> and <see cref="Unmatched"/> are terminal (the last two are reopenable by a
/// manual import). An operation failure returns the job to <see cref="Pending"/> so it retries
/// without duplicating.
/// </summary>
public enum ImportJobState
{
    Pending = 1,
    Matching = 2,
    Deciding = 3,
    Operating = 4,
    Probing = 5,
    Registered = 6,
    Rejected = 7,
    Unmatched = 8,
}

/// <summary>
/// The lifecycle of a <b>single matched file</b> inside an import job. The job's own state machine is
/// a <em>batch</em> machine traversed once for the whole set (Matching → Deciding → Operating →
/// Probing → Registered); this one tracks where each individual file got to, which is what makes a
/// season pack — where three of ten files may land and seven fail — recoverable without re-linking
/// what already verified.
/// <para>
/// <see cref="Unresolved"/> is the "landed nowhere" exit: the file carries numbering that maps to no
/// catalog unit inside the requested scope, so it is deliberately left alone rather than linked to an
/// episode nobody asked for. <see cref="Failed"/> is reopenable — a re-drive resets it to
/// <see cref="Planned"/> and tries again.
/// </para>
/// Persisted as text, so members may only ever be appended with an explicit value.
/// </summary>
public enum ImportFileMatchState
{
    Planned = 1,
    Operated = 2,
    Probed = 3,
    Registered = 4,
    Failed = 5,
    Unresolved = 6,
}

/// <summary>The kind of a recoverable file operation, as recorded on each <c>FileOperation</c>.</summary>
public enum FileOperationType
{
    Hardlink = 1,
    Copy = 2,
    Move = 3,
    Rename = 4,

    /// <summary>
    /// Setting a superseded file aside before its replacement lands on the same path. Its own type
    /// rather than a <see cref="Move"/> so the job's trail says plainly what happened to a file the
    /// household still owns and may want back.
    /// </summary>
    Recycle = 5,

    /// <summary>
    /// The file is in place and the platform would not say whether it shares its bytes with the download
    /// or holds its own copy. Only a job resumed after a restart can reach this: the file landed during an
    /// attempt that never got to write its row, and the filesystem was then unable to supply the identity
    /// that answers the question (an operating system this project does not target, a filesystem that
    /// numbers no files, or a read that failed).
    /// <para>
    /// It exists so that the record can be honest rather than plausible. A <see cref="Hardlink"/> asserted
    /// here would tell an operator the bytes are shared when removing the download may leave the library
    /// with nothing, and a <see cref="Copy"/> would claim a second full copy of the file that is not
    /// there. Neither is knowable, so neither is written.
    /// </para>
    /// </summary>
    Unknown = 6,
}

/// <summary>
/// The lifecycle of a single recoverable file operation. It is planned before it runs, marked
/// verified once its result is confirmed on disk, and rolled back (or failed) otherwise — so a
/// mid-flight restart resumes from the last confirmed step instead of re-duplicating.
/// </summary>
public enum FileOperationState
{
    Planned = 1,
    Executing = 2,
    Verified = 3,
    RolledBack = 4,
    Failed = 5,
}

/// <summary>The kind of an elementary media stream reported by ffprobe.</summary>
public enum MediaStreamKind
{
    Video = 1,
    Audio = 2,
    Subtitle = 3,
}

/// <summary>
/// One elementary stream of an analysed file (part of the <c>MediaAvailable</c> published language).
/// Nullable geometry/channel fields apply only to the relevant kind. Library persists these
/// relationally; Import only produces them.
/// <para>
/// <see cref="VideoRange"/> is a video stream's dynamic range by name — <c>Sdr</c>, <c>Hdr10</c>,
/// <c>Hlg</c> or <c>DoVi</c> — and null for any other stream or an event published before it was read.
/// </para>
/// </summary>
public sealed record MediaStreamInfo(
    int Index,
    MediaStreamKind Kind,
    string Codec,
    string? Language,
    int? Width,
    int? Height,
    int? Channels,
    bool IsDefault,
    bool IsForced,
    string? VideoRange = null);

/// <summary>
/// The result of analysing a media file with ffprobe: the container plus its elementary streams.
/// Carried by <c>MediaAvailable</c> and stored on the job for observability. A probe failure yields
/// a value with an empty <see cref="Streams"/> list (the asset still registers, rule from the FSM).
/// </summary>
public sealed record MediaInfo(
    string Container,
    double DurationSeconds,
    long Bitrate,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    public static MediaInfo Empty { get; } = new(string.Empty, 0, 0, []);
}

/// <summary>
/// The structural quality read off a release name, carried by <c>MediaAvailable</c> as plain wire
/// values (the enum names ReleaseParsing produces). It rides the event because nothing downstream can
/// recover it: <see cref="MediaInfo"/> is an ffprobe result, and geometry tells you a file is 1080p
/// while saying nothing about whether it came from a disc or a stream — the very distinction an upgrade
/// turns on. By the time Library stores the file, a series episode has been renamed and its release
/// name is gone.
/// </summary>
public sealed record ReleaseQualityInfo(string? Source, string? Resolution, string? Modifier, int Revision);

/// <summary>
/// Projection of an import job's current state. <see cref="TargetPath"/> and <see cref="AssetId"/>
/// are <b>first-file shims</b> — a season pack lands many; read <see cref="ImportJobDetail.Matches"/>
/// for the per-file trail, and <see cref="FileCount"/> to know there is one.
/// </summary>
public sealed record ImportJobSummary(
    ImportJobId Id,
    Guid DownloadTaskId,
    Guid IntentId,
    ImportJobState State,
    string SourcePath,
    string? TargetPath,
    Guid? AssetId,
    string? Reason,
    int FileCount = 0);

/// <summary>
/// Projection of one file inside an import job: where it came from, where it landed, the asset it
/// registered as and the catalog units it serves. This is the observability trail a season pack
/// needs — reporting one arbitrary <c>targetPath</c> for a ten-episode job explains nothing.
/// </summary>
public sealed record ImportFileMatchSummary(
    int Seq,
    string SourcePath,
    long Size,
    string? TargetPath,
    Guid AssetId,
    IReadOnlyList<Guid> UnitIds,
    int? SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    ImportFileMatchState State,
    string? Reason);

/// <summary>Projection of one recoverable file operation and its outcome.</summary>
public sealed record FileOperationSummary(
    int Seq,
    FileOperationType Type,
    string FromPath,
    string ToPath,
    FileOperationState State,
    bool Verified);

/// <summary>One immutable line of the job's transition history (append-only, observability).</summary>
public sealed record ImportHistoryEntry(
    int Seq,
    ImportJobState From,
    ImportJobState To,
    string Trigger,
    DateTimeOffset OccurredAt,
    string? Note);

/// <summary>
/// The full view of an import job: its state, the files it matched, its file operations, and its
/// history. <see cref="Matches"/> is trailing optional so existing construction sites are unaffected.
/// </summary>
public sealed record ImportJobDetail(
    ImportJobSummary Job,
    MediaInfo? MediaInfo,
    IReadOnlyList<FileOperationSummary> Operations,
    IReadOnlyList<ImportHistoryEntry> History,
    IReadOnlyList<ImportFileMatchSummary>? Matches = null);
