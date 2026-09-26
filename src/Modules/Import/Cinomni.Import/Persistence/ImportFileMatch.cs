using System.ComponentModel.DataAnnotations.Schema;
using Cinomni.Import.Contracts;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Import.Persistence;

/// <summary>
/// One file matched inside an import job, keyed
/// <c>(ImportJobId, Seq)</c> exactly like <see cref="FileOperation"/> and
/// <see cref="ImportHistoryRecord"/>. <b>All</b> per-file state lives here: where the file came from,
/// its fingerprint, where it landed, the catalog units it serves, its ffprobe result and the asset it
/// registered as. The job root keeps only first-match shims for the observability surface.
/// <para>
/// <see cref="AssetId"/> is minted <b>once</b>, when the match is first recorded, and persisted. It is
/// deliberately not re-minted on a retry: <c>ux_media_versions_full_path</c> is unique on the landed
/// path, so a fresh id would slip past Library's asset-id idempotency and then fail the whole unit of
/// work on the duplicate version.
/// </para>
/// <para>
/// A match may carry <b>several</b> <see cref="UnitIds"/>: a file named <c>S01E01-E02</c> is one
/// physical file serving two episodes, and the unique index on the version path makes two assets for
/// one file impossible. Playback of such a file therefore always starts at the beginning; per-episode
/// offsets are explicitly deferred.
/// </para>
/// </summary>
public sealed class ImportFileMatch
{
    private const int PathMaxLength = 2048;
    private const int HashMaxLength = 128;
    private const int ReasonMaxLength = 500;

    public Guid ImportJobId { get; init; }

    /// <summary>Ordinal within the job (a season pack records several; a movie records one).</summary>
    public int Seq { get; init; }

    /// <summary>The file inside the download's content path this match covers.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Size in bytes, used to verify the file operation landed intact.</summary>
    public long Size { get; private set; }

    /// <summary>Content fingerprint — matching is by content, not name.</summary>
    public string? Hash { get; private set; }

    /// <summary>Where the file landed in the library (set once its operation verifies).</summary>
    public string? TargetPath { get; private set; }

    /// <summary>The <see cref="FileOperation.Seq"/> planned for this file, if one was planned yet.</summary>
    public int? OperationSeq { get; private set; }

    /// <summary>The asset identity this file registers as. Minted once, reused on every retry.</summary>
    public Guid AssetId { get; init; }

    /// <summary>The catalog units this file serves (one episode, two for a multi-episode file, or the work).</summary>
    public Guid[] UnitIds { get; private set; } = [];

    /// <summary>Season the file belongs to, when it was resolved to one. Null on the movie path.</summary>
    public int? SeasonNumber { get; private set; }

    /// <summary>Episodes the file covers, when they were resolved. Empty on the movie path.</summary>
    public int[] EpisodeNumbers { get; private set; } = [];

    /// <summary>Serialized <see cref="Contracts.MediaInfo"/> (jsonb). Read through <see cref="MediaInfo"/>.</summary>
    public string? MediaInfoJson { get; private set; }

    public ImportFileMatchState State { get; private set; } = ImportFileMatchState.Planned;

    /// <summary>Why this file failed or resolved to nothing, if it did.</summary>
    public string? Reason { get; private set; }

    /// <summary>The analysed media info, projected from <see cref="MediaInfoJson"/>.</summary>
    [NotMapped]
    public MediaInfo? MediaInfo => MediaInfoSerializer.FromJson(MediaInfoJson);

    /// <summary>True once the file is physically in the library — it must never be re-linked.</summary>
    [NotMapped]
    public bool IsLanded => State is ImportFileMatchState.Operated
        or ImportFileMatchState.Probed
        or ImportFileMatchState.Registered;

    /// <summary>Records a newly matched file, minting the asset id that every retry will reuse.</summary>
    public static ImportFileMatch Plan(Guid importJobId, int seq, string sourcePath, long size, string? hash) =>
        new()
        {
            ImportJobId = importJobId,
            Seq = seq,
            SourcePath = Text.Truncate(sourcePath, PathMaxLength)!,
            Size = size,
            Hash = Text.Truncate(hash, HashMaxLength),
            AssetId = Uuid7.New(),
            State = ImportFileMatchState.Planned,
        };

    /// <summary>Refreshes the fingerprint of an already-recorded file (a re-drive re-scans the content).</summary>
    public void RefreshFingerprint(long size, string? hash)
    {
        Size = size;
        Hash = Text.Truncate(hash, HashMaxLength);
    }

    /// <summary>Records the catalog units this file serves, with the numbers a consumer needs to name it.</summary>
    public void ResolveUnits(IReadOnlyList<Guid> unitIds, int? seasonNumber, IReadOnlyList<int> episodeNumbers)
    {
        UnitIds = [.. unitIds];
        SeasonNumber = seasonNumber;
        EpisodeNumbers = [.. episodeNumbers];
    }

    /// <summary>Attaches the recoverable operation planned for this file and the path it targets.</summary>
    public void AttachOperation(int operationSeq, string targetPath)
    {
        OperationSeq = operationSeq;
        TargetPath = Text.Truncate(targetPath, PathMaxLength);
    }

    /// <summary>Planned → Operated: the hardlink landed and verified at <paramref name="targetPath"/>.</summary>
    public void MarkOperated(string targetPath)
    {
        Require(ImportFileMatchState.Planned, ImportFileMatchState.Operated);
        TargetPath = Text.Truncate(targetPath, PathMaxLength);
        Reason = null;
        State = ImportFileMatchState.Operated;
    }

    /// <summary>Operated → Probed: ffprobe ran (an empty result is still a result — the asset registers).</summary>
    public void MarkProbed(MediaInfo mediaInfo)
    {
        Require(ImportFileMatchState.Operated, ImportFileMatchState.Probed);
        MediaInfoJson = MediaInfoSerializer.ToJson(mediaInfo);
        State = ImportFileMatchState.Probed;
    }

    /// <summary>Probed → Registered: the asset was announced under the id minted at match time.</summary>
    public void MarkRegistered()
    {
        Require(ImportFileMatchState.Probed, ImportFileMatchState.Registered);
        State = ImportFileMatchState.Registered;
    }

    /// <summary>Planned → Failed: this file's operation could not be verified. Reopenable.</summary>
    public void MarkFailed(string reason)
    {
        Require(ImportFileMatchState.Planned, ImportFileMatchState.Failed);
        Reason = Text.Truncate(reason, ReasonMaxLength);
        State = ImportFileMatchState.Failed;
    }

    /// <summary>Planned → Unresolved: the file maps to no catalog unit inside the requested scope.</summary>
    public void MarkUnresolved(string reason)
    {
        Require(ImportFileMatchState.Planned, ImportFileMatchState.Unresolved);
        Reason = Text.Truncate(reason, ReasonMaxLength);
        State = ImportFileMatchState.Unresolved;
    }

    /// <summary>
    /// Returns a failed or unresolved file to <see cref="ImportFileMatchState.Planned"/> so a re-drive
    /// retries it. A landed file is never reopened — that is what stops a retry re-linking the set.
    /// </summary>
    public bool Reopen()
    {
        if (State is not (ImportFileMatchState.Failed or ImportFileMatchState.Unresolved))
        {
            return false;
        }

        State = ImportFileMatchState.Planned;
        OperationSeq = null;
        return true;
    }

    private void Require(ImportFileMatchState from, ImportFileMatchState to)
    {
        if (State != from)
        {
            throw new InvalidOperationException(
                $"Illegal file-match transition {State}→{to} (expected from {from}) for job {ImportJobId} file {Seq}.");
        }
    }
}
