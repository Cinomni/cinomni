using Cinomni.Kernel.Identifiers;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Persistence;

/// <summary>
/// An obtained subtitle: the file that landed next to the video plus its language flags and
/// provenance. References the media asset by id (inter-schema, no physical FK). Library is told about
/// it through <c>SubtitleAvailable</c> and enriches its own asset — Subtitles never writes Library's
/// tables.
/// </summary>
public sealed class SubtitleAsset
{
    private const int LanguageMaxLength = 20;
    private const int PathMaxLength = 2048;
    private const int ProviderMaxLength = 200;

    public Guid Id { get; init; }

    public Guid AssetId { get; init; }

    public Guid SearchId { get; init; }

    public required string Language { get; init; }

    public bool Forced { get; init; }

    public bool HearingImpaired { get; init; }

    public SubtitleFormat Format { get; init; }

    /// <summary>
    /// The file played: the download as it landed, or the synchronized copy beside it. Settable only from
    /// inside the entity (see <see cref="Relocate"/>, <see cref="UseSynced"/>, <see cref="UseOriginal"/>):
    /// the file is named from the video's stem and lives in its directory, so it moves when the video does
    /// and this row has to follow it.
    /// </summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>
    /// The file as it was downloaded. Synchronization never writes to it: it is what every analysis reads,
    /// so a second pass starts from the provider's timing rather than compounding the first one's shift,
    /// and it is what a revert puts back.
    /// </summary>
    public string OriginalPath { get; private set; } = string.Empty;

    /// <summary>Whether the file played is a synchronized copy rather than the download itself.</summary>
    public bool IsSynced => !string.Equals(Path, OriginalPath, StringComparison.Ordinal);

    public required string Provider { get; init; }

    public int Score { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static SubtitleAsset Create(
        Guid assetId,
        Guid searchId,
        string language,
        bool forced,
        bool hearingImpaired,
        SubtitleFormat format,
        string path,
        string provider,
        int score,
        DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        AssetId = assetId,
        SearchId = searchId,
        Language = Text.Truncate(language, LanguageMaxLength)!,
        Forced = forced,
        HearingImpaired = hearingImpaired,
        Format = format,
        Path = Text.Truncate(path, PathMaxLength)!,
        OriginalPath = Text.Truncate(path, PathMaxLength)!,
        Provider = Text.Truncate(provider, ProviderMaxLength)!,
        Score = score,
        CreatedAt = now,
    };

    /// <summary>
    /// Records that the sidecars now sit at <paramref name="path"/> and <paramref name="originalPath"/>,
    /// after Import moved them along with the video they belong to. Both move: a synchronized copy is a
    /// sidecar of the same video. Idempotent: repeating it with the stored paths changes nothing.
    /// </summary>
    public void Relocate(string path, string originalPath)
    {
        Path = Text.Truncate(path, PathMaxLength)!;
        OriginalPath = Text.Truncate(originalPath, PathMaxLength)!;
    }

    /// <summary>Plays the synchronized copy at <paramref name="syncedPath"/> from now on; the original stays.</summary>
    public void UseSynced(string syncedPath) => Path = Text.Truncate(syncedPath, PathMaxLength)!;

    /// <summary>Plays the file as downloaded again.</summary>
    public void UseOriginal() => Path = OriginalPath;
}
