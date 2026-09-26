using Cinomni.Kernel.Messaging;

namespace Cinomni.Subtitles.Messaging;

/// <summary>Stable registered names of the Subtitles commands.</summary>
public static class SubtitleCommandNames
{
    public const string SearchSubtitles = "subtitles.search";
    public const string RelocateSubtitles = "subtitles.relocate";
    public const string CatchUp = "subtitles.catch-up";
}

/// <summary>
/// Asks the library for subtitles the current preference still lacks. Parameterless so the scheduler
/// can construct it. A redelivery adds nothing a previous pass already recorded.
/// </summary>
public sealed record CatchUpSubtitlesCommand : ICommand;

/// <summary>
/// Points an asset's sidecars at the paths they were moved to with their video — enqueued by Import's
/// <c>MediaFileRelocated</c> reaction. Idempotent by <c>relocate-subtitles:{assetId}</c>, and again in
/// the handler, which recomposes each path from the video's stem and writes only what changed.
/// </summary>
public sealed record RelocateSubtitlesCommand(Guid AssetId, string FromVideoPath, string ToVideoPath) : ICommand;

/// <summary>
/// Searches for the missing subtitle languages of a registered asset — enqueued by Library's
/// <c>MediaAssetRegistered</c> reaction. The provider fetch and file write are out-of-process, so the
/// work must be retryable; idempotent by <c>search-subtitles:{assetId}</c>.
/// </summary>
public sealed record SearchSubtitlesCommand(Guid AssetId) : ICommand;
