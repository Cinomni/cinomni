using Cinomni.Kernel.Messaging;

namespace Cinomni.Import.Contracts;

/// <summary>Stable registered names of the Import integration events.</summary>
public static class ImportEventNames
{
    public const string ImportRequested = "import.requested";
    public const string ImportCompleted = "import.completed";
    public const string ImportFailed = "import.failed";
    public const string MediaAvailable = "import.media-available";
    public const string MediaFileRelocated = "import.media-file-relocated";
}

/// <summary>
/// An import job was opened for a completed download (the job enters <c>Pending</c>). Recovery/audit
/// marker. Ids travel as raw <see cref="Guid"/> for wire stability; keyed by the job.
/// </summary>
public sealed record ImportRequested(Guid ImportJobId, Guid DownloadTaskId) : DomainEvent
{
    public override string IdempotencyKey => $"import-requested:{ImportJobId}";
}

/// <summary>
/// A download was imported and registered as a media asset. Echoes the acquisition correlation
/// (<see cref="IntentId"/>/<see cref="AttemptId"/>, carried through <c>DownloadCompleted</c>) so
/// Acquisition can advance the matching goal to <c>Available</c>. The library/catalog-facing
/// identity (<see cref="AssetId"/>) rides along; the <c>workId</c>/<c>targetIds</c> enrichment
/// arrives with the Library module that consumes it. Keyed by the job.
/// </summary>
/// <param name="UnitIds">
/// The catalog units the job landed content for. Trailing optional: the payload is persisted as
/// jsonb, so an in-flight 1.x row deserializes with it null.
/// </param>
public sealed record ImportCompleted(
    Guid ImportJobId,
    Guid IntentId,
    Guid AttemptId,
    Guid AssetId,
    IReadOnlyList<Guid>? UnitIds = null) : DomainEvent
{
    public override string IdempotencyKey => $"import-completed:{ImportJobId}";
}

/// <summary>
/// An import failed (specs rejected it, no file matched, or a file operation could not be verified
/// and was rolled back). Echoes the acquisition correlation so Acquisition returns the goal to
/// searching or exhausts it (case #6). Keyed by the job.
/// </summary>
public sealed record ImportFailed(
    Guid ImportJobId,
    Guid IntentId,
    Guid AttemptId,
    string Reason) : DomainEvent
{
    public override string IdempotencyKey => $"import-failed:{ImportJobId}";
}

/// <summary>
/// A media asset is now on disk with its analysed streams. Consumed by Library (persist the asset +
/// its version + relational streams — it owns the <c>library.*</c> rows) and Catalog (mark the work
/// available). Carries everything a consumer needs to act without reaching back (event-catalog
/// "over data, not pointers"): the work/target correlation, the landed file, and the ffprobe result.
/// Keyed by the asset.
/// </summary>
/// <param name="UnitIds">
/// The catalog units this asset serves — the work id for a movie, one or more episode ids for a
/// series file. <see cref="TargetIds"/> stays what it always was (the acquiring monitored targets);
/// this is the content-side correlation. Trailing optional: the payload is persisted as jsonb, so
/// an in-flight 1.x row deserializes with it null.
/// </param>
/// <param name="SeasonNumber">
/// Season the asset belongs to, so a consumer can say "S02E05 of The Wire is ready" without a
/// Catalog round-trip (this event carries data, not pointers). Null on the movie path.
/// </param>
/// <param name="EpisodeNumbers">
/// Episodes the file covers (more than one for a multi-episode file). Null on the movie path.
/// </param>
/// <param name="Quality">
/// The structural quality parsed from the release name. Null when the name did not parse; trailing
/// optional, so an in-flight row deserializes with it null and the version's source stays unknown.
/// </param>
public sealed record MediaAvailable(
    Guid AssetId,
    Guid WorkId,
    IReadOnlyList<Guid> TargetIds,
    Guid ImportJobId,
    Guid DownloadTaskId,
    string FullPath,
    long Size,
    MediaInfo MediaInfo,
    IReadOnlyList<Guid>? UnitIds = null,
    int? SeasonNumber = null,
    IReadOnlyList<int>? EpisodeNumbers = null,
    ReleaseQualityInfo? Quality = null) : DomainEvent
{
    public override string IdempotencyKey => $"media-available:{AssetId}";
}

/// <summary>
/// A file already in the library was moved, because the name an earlier build wrote carried characters
/// the naming rules now repair. Import owns the library tree and therefore performs the move; the
/// modules that persist a path react to this and correct their own rows — Library the media version,
/// Subtitles the sidecars that sit next to the video and are named from its stem.
/// <para>
/// The move happens outside any database transaction, as every filesystem effect does, so this event
/// is what makes the repair recoverable: a crash between the move and the event leaves the file at its
/// new path with the rows still naming the old one, and the next pass sees that state, finds nothing
/// at the stored path and the file at the sanitised one, and emits this after the fact. Keyed by the
/// asset, since the sanitised path is a pure function of the old one and a second repair of the same
/// asset is the same repair.
/// </para>
/// </summary>
/// <param name="FromPath">Where the file was, i.e. the path consumers still have stored.</param>
/// <param name="ToPath">Where it is now.</param>
public sealed record MediaFileRelocated(Guid AssetId, string FromPath, string ToPath) : DomainEvent
{
    public override string IdempotencyKey => $"media-file-relocated:{AssetId}";
}
