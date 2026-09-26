using Cinomni.Kernel.Messaging;

namespace Cinomni.Subtitles.Contracts;

/// <summary>Stable registered names of the Subtitles integration events.</summary>
public static class SubtitleEventNames
{
    public const string SubtitleSearchRequested = "subtitles.search-requested";
    public const string SubtitleAvailable = "subtitles.available";
    public const string SubtitleSearchFailed = "subtitles.search-failed";
}

/// <summary>A subtitle search was opened for a missing language on an asset. Recovery/audit marker. Keyed by the search.</summary>
public sealed record SubtitleSearchRequested(
    Guid SearchId,
    Guid AssetId,
    string Language) : DomainEvent
{
    public override string IdempotencyKey => $"subtitle-search-requested:{SearchId}";
}

/// <summary>
/// A subtitle was obtained and written to disk. Consumed by Library (enrich the asset with an external
/// subtitle stream) and Notifications. Carries the language flags and the file path. Keyed by the
/// subtitle.
/// </summary>
public sealed record SubtitleAvailable(
    Guid SubtitleId,
    Guid AssetId,
    string Language,
    bool Forced,
    bool HearingImpaired,
    string Path) : DomainEvent
{
    public override string IdempotencyKey => $"subtitle-available:{SubtitleId}";
}

/// <summary>
/// A subtitle search found nothing over the score threshold. The search waits for an adaptive
/// re-search rather than failing outright. Keyed by search+attempt.
/// </summary>
public sealed record SubtitleSearchFailed(
    Guid SearchId,
    Guid AssetId,
    string Language,
    int Attempt) : DomainEvent
{
    public override string IdempotencyKey => $"subtitle-search-failed:{SearchId}:{Attempt}";
}
