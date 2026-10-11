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
/// an adaptive re-search. A landed SubRip or WebVTT file then passes through <see cref="SubtitleSearchState.Syncing"/>,
/// where its timing is checked against the video's audio; the subtitle is playable throughout, and the
/// step ends in <see cref="SubtitleSearchState.Available"/> whatever it decides. Upgrades come with a later slice.
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
    int Attempts,
    Guid? SubtitleId = null);

/// <summary>Projection of one scored candidate found for a search.</summary>
public sealed record SubtitleCandidateSummary(
    string Provider,
    string Release,
    int Score,
    bool HearingImpaired);

/// <summary>
/// What a synchronization pass decided about a subtitle's timing. Only <see cref="Applied"/> changes
/// which file is played; every other outcome keeps the file as it was downloaded, and says why.
/// </summary>
public enum SubtitleSyncOutcome
{
    /// <summary>A shifted copy was written and is now the file played.</summary>
    Applied = 1,

    /// <summary>The best match was within a tenth of a second at the same speed: nothing to change.</summary>
    AlreadyInSync = 2,

    /// <summary>A match was found but not clearly enough to trust it; the original is kept.</summary>
    LowConfidence = 3,

    /// <summary>This subtitle or video cannot be synchronized (format, forced track, no audio, switched off).</summary>
    Unsupported = 4,

    /// <summary>The analysis could not run (FFmpeg missing, timeout, unreadable file); the original is kept.</summary>
    Failed = 5,

    /// <summary>An administrator put the original file back.</summary>
    Reverted = 6,
}

/// <summary>
/// The latest synchronization decision for an obtained subtitle. <see cref="OffsetMs"/> and
/// <see cref="Ratio"/> describe the best match the analysis found, applied or not:
/// a cue at <c>t</c> in the original plays at <c>t × Ratio + OffsetMs</c>. <see cref="HasOriginal"/>
/// says the file played is a synchronized copy, so the original can be put back.
/// </summary>
public sealed record SubtitleSyncSummary(
    SubtitleSyncOutcome Outcome,
    int OffsetMs,
    double Ratio,
    double Score,
    string Reason,
    bool HasOriginal,
    DateTimeOffset DecidedAt);

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
    int Score,
    SubtitleSyncSummary? Sync = null);

/// <summary>The full view of a search: its state, its candidates, and the subtitle it landed (if any).</summary>
public sealed record SubtitleSearchDetail(
    SubtitleSearchSummary Search,
    IReadOnlyList<SubtitleCandidateSummary> Candidates,
    SubtitleAssetSummary? Asset);
