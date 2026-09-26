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
    /// Where the sidecar sits. Settable only from inside the entity (see <see cref="Relocate"/>): the
    /// file is named from the video's stem and lives in its directory, so it moves when the video does
    /// and this row has to follow it.
    /// </summary>
    public string Path { get; private set; } = string.Empty;

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
        Provider = Text.Truncate(provider, ProviderMaxLength)!,
        Score = score,
        CreatedAt = now,
    };

    /// <summary>
    /// Records that the sidecar now sits at <paramref name="path"/>, after Import moved it along with
    /// the video it belongs to. Idempotent: repeating it with the stored path changes nothing.
    /// </summary>
    public void Relocate(string path) => Path = Text.Truncate(path, PathMaxLength)!;
}
