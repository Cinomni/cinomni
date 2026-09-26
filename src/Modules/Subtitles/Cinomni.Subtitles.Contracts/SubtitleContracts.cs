using Cinomni.Kernel.Identifiers;

namespace Cinomni.Subtitles.Contracts;

/// <summary>Stable internal identity of a subtitle search (UUIDv7).</summary>
public readonly record struct SubtitleSearchId(Guid Value)
{
    public static SubtitleSearchId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>Stable internal identity of an obtained subtitle asset (UUIDv7).</summary>
public readonly record struct SubtitleAssetId(Guid Value)
{
    public static SubtitleAssetId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// The lifecycle of a subtitle search. It is requested, searches providers, scores
/// the candidates, downloads the best over the threshold and lands it — or finds nothing and waits for
/// an adaptive re-search. Sync (ffsubsync) and upgrades come with a later slice.
/// </summary>
public enum SubtitleSearchState
{
    Requested = 1,
    Searching = 2,
    Evaluated = 3,
    Downloading = 4,
    Syncing = 5,
    NotFound = 6,
    Available = 7,
}

/// <summary>The subtitle container format on disk.</summary>
public enum SubtitleFormat
{
    Srt = 1,
    Ass = 2,
    Vtt = 3,
}

/// <summary>Projection of a subtitle search's current state.</summary>
public sealed record SubtitleSearchSummary(
    SubtitleSearchId Id,
    Guid AssetId,
    string Language,
    bool Forced,
    bool HearingImpaired,
    SubtitleSearchState State,
    int Attempts);

/// <summary>Projection of one scored candidate found for a search.</summary>
public sealed record SubtitleCandidateSummary(
    string Provider,
    string Release,
    int Score,
    bool HearingImpaired);

/// <summary>Projection of an obtained subtitle: the file that landed and its language flags.</summary>
public sealed record SubtitleAssetSummary(
    SubtitleAssetId Id,
    Guid AssetId,
    string Language,
    bool Forced,
    bool HearingImpaired,
    SubtitleFormat Format,
    string Path,
    string Provider,
    int Score);

/// <summary>The full view of a search: its state, its candidates, and the subtitle it landed (if any).</summary>
public sealed record SubtitleSearchDetail(
    SubtitleSearchSummary Search,
    IReadOnlyList<SubtitleCandidateSummary> Candidates,
    SubtitleAssetSummary? Asset);
