using Cinomni.Import.Contracts;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Library.Messaging;
using Cinomni.Operations.Messaging;

namespace Cinomni.Library.EventHandlers;

/// <summary>
/// Reacts to Import's <see cref="MediaAvailable"/> by registering the asset in the library. Enqueues
/// a command (the handler runs in the outbox relay's transaction, so the library write happens in its
/// own unit of work); idempotent by <c>register-media-asset:{assetId}</c>. Import owns the pipeline
/// and publishes the asset; Library owns and persists the rows.
/// </summary>
public sealed class MediaAvailableHandler(ICommandQueue commandQueue) : IEventHandler<MediaAvailable>
{
    public async Task HandleAsync(MediaAvailable domainEvent, CancellationToken cancellationToken = default)
    {
        var streams = domainEvent.MediaInfo.Streams
            .Select(s => new MediaStreamInput(
                s.Index,
                MapKind(s.Kind),
                s.Codec,
                s.Language,
                s.Channels,
                s.Width,
                s.Height,
                BitDepth: null,        // ffprobe bit-depth extraction is a later enrichment
                RangeOf(s.VideoRange),
                s.IsDefault,
                s.IsForced))
            .ToList();

        await commandQueue.EnqueueAsync(
            new RegisterMediaAssetCommand(
                domainEvent.AssetId,
                domainEvent.WorkId,
                domainEvent.TargetIds,
                domainEvent.FullPath,
                domainEvent.Size,
                domainEvent.MediaInfo.Container,
                streams,
                domainEvent.UnitIds,
                MapQuality(domainEvent.Quality),
                // A failed probe reports zeros: "unknown", not a zero-length file.
                domainEvent.MediaInfo.DurationSeconds > 0 ? domainEvent.MediaInfo.DurationSeconds : null,
                domainEvent.MediaInfo.Bitrate > 0 ? domainEvent.MediaInfo.Bitrate : null),
            idempotencyKey: $"register-media-asset:{domainEvent.AssetId}",
            cancellationToken);
    }

    /// <summary>
    /// A range Import named, or null. Only an exact member name counts: <c>Enum.TryParse</c> alone would
    /// also take "99" or "2,4" from the wire and store a value the enumeration does not have.
    /// </summary>
    private static VideoRangeType? RangeOf(string? name) =>
        name is not null
        && Enum.GetNames<VideoRangeType>().Contains(name, StringComparer.Ordinal)
        && Enum.TryParse<VideoRangeType>(name, ignoreCase: false, out var range)
            ? range
            : null;

    /// <summary>Import's wire shape into Library's own, or null when the release name said nothing.</summary>
    private static ReleaseQuality? MapQuality(ReleaseQualityInfo? quality) =>
        quality is null ? null : new ReleaseQuality(quality.Source, quality.Resolution, quality.Modifier, quality.Revision);

    private static MediaStreamType MapKind(MediaStreamKind kind) => kind switch
    {
        MediaStreamKind.Video => MediaStreamType.Video,
        MediaStreamKind.Audio => MediaStreamType.Audio,
        _ => MediaStreamType.Subtitle,
    };
}
