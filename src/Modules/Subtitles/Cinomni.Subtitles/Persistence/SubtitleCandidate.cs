namespace Cinomni.Subtitles.Persistence;

/// <summary>
/// One scored candidate a provider returned for a search (append-only, keyed by <c>(SearchId, Seq)</c>).
/// The <see cref="DownloadRef"/> is the provider's opaque handle used to fetch the file.
/// </summary>
public sealed class SubtitleCandidate
{
    private const int TextMaxLength = 200;
    private const int RefMaxLength = 500;

    public Guid SearchId { get; init; }

    public int Seq { get; init; }

    public required string Provider { get; init; }

    public required string Release { get; init; }

    public int Score { get; init; }

    public bool HearingImpaired { get; init; }

    public required string DownloadRef { get; init; }

    public static SubtitleCandidate Create(
        Guid searchId, int seq, string provider, string release, int score, bool hearingImpaired, string downloadRef) => new()
    {
        SearchId = searchId,
        Seq = seq,
        Provider = Text.Truncate(provider, TextMaxLength)!,
        Release = Text.Truncate(release, TextMaxLength)!,
        Score = score,
        HearingImpaired = hearingImpaired,
        DownloadRef = Text.Truncate(downloadRef, RefMaxLength)!,
    };
}
