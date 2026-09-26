using System.ComponentModel.DataAnnotations.Schema;
using Cinomni.Kernel.Identifiers;
using Cinomni.Library.Contracts;

namespace Cinomni.Library.Persistence;

/// <summary>
/// A version of an asset — an edition/stack of a file, the middle of
/// the <c>MediaAsset → MediaVersion → MediaStream</c> hierarchy. Among live versions its
/// <see cref="FullPath"/> is unique (no two of them name the same file). A version is retired when its
/// asset is upgraded away, and then it is history: the replacement usually lands on the very same path,
/// which the retired row keeps as the record of where the old copy was.
/// </summary>
public sealed class MediaVersion
{
    private const int PathMaxLength = 2048;
    private const int ReleaseGroupMaxLength = 200;

    public Guid Id { get; init; }

    public Guid AssetId { get; init; }

    /// <summary>
    /// The file name, and the two paths a repair pass may correct. Settable only from inside the
    /// entity (see <see cref="Relocate"/>) — a path is otherwise fixed for the life of a version, and
    /// the one legitimate reason to change it is the file having been moved on disk first.
    /// </summary>
    public string RelativePath { get; private set; } = string.Empty;

    public string FullPath { get; private set; } = string.Empty;

    public long Size { get; init; }

    /// <summary>Serialized <see cref="MediaQuality"/> (jsonb). Read through <see cref="Quality"/>.</summary>
    public string QualityJson { get; init; } = MediaQuality.Unknown.ToJson();

    public string? ReleaseGroup { get; init; }

    /// <summary>
    /// The runtime ffprobe measured, in seconds; null when the probe could not say (or for a version
    /// registered before it was recorded). Playback draws a full-length timeline from it when the
    /// stream it serves starts part way into the file.
    /// </summary>
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// The container's overall bitrate in bits per second, as ffprobe measured it; null when unknown.
    /// It is what tells Playback whether the file already fits a quality the viewer asked for.
    /// </summary>
    public long? Bitrate { get; init; }

    public List<MediaStream> Streams { get; } = [];

    /// <summary>When the version stopped being a live file of the library; null while it is one.</summary>
    public DateTimeOffset? RetiredAt { get; private set; }

    /// <summary>The quality value-object, projected from <see cref="QualityJson"/>.</summary>
    [NotMapped]
    public MediaQuality Quality => MediaQuality.FromJson(QualityJson);

    public static MediaVersion Create(
        Guid assetId,
        string relativePath,
        string fullPath,
        long size,
        MediaQuality quality,
        string? releaseGroup,
        IReadOnlyList<MediaStreamInput> streams,
        double? durationSeconds = null,
        long? bitrate = null)
    {
        var version = new MediaVersion
        {
            Id = Uuid7.New(),
            AssetId = assetId,
            RelativePath = Text.Truncate(relativePath, PathMaxLength)!,
            FullPath = Text.Truncate(fullPath, PathMaxLength)!,
            Size = size,
            QualityJson = quality.ToJson(),
            ReleaseGroup = Text.Truncate(releaseGroup, ReleaseGroupMaxLength),
            DurationSeconds = durationSeconds is > 0 and < double.MaxValue ? durationSeconds : null,
            Bitrate = bitrate is > 0 ? bitrate : null,
        };

        foreach (var stream in streams)
        {
            version.Streams.Add(MediaStream.FromInput(version.Id, stream));
        }

        return version;
    }

    /// <summary>Takes the version out of the live library. Idempotent: the first retirement is the one recorded.</summary>
    public void Retire(DateTimeOffset now) => RetiredAt ??= now;

    /// <summary>
    /// Records that the file now sits at <paramref name="fullPath"/>. Called when Import has already
    /// moved it — never the other way round, because a row that names a path the disk does not have is
    /// a library that offers a file nobody can play.
    /// <para>
    /// Idempotent: repeating it with the path already stored changes nothing, which is what lets the
    /// command carrying it be delivered more than once.
    /// </para>
    /// </summary>
    public void Relocate(string fullPath)
    {
        FullPath = Text.Truncate(fullPath, PathMaxLength)!;
        RelativePath = Text.Truncate(Path.GetFileName(FullPath), PathMaxLength)!;
    }
}
