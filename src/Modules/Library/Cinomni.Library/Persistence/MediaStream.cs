using Cinomni.Library.Contracts;

namespace Cinomni.Library.Persistence;

/// <summary>
/// One elementary media stream, stored <b>relationally</b> (deliberately not a JSON blob of
/// stream data): a queryable row per track. Belongs to a <see cref="MediaVersion"/>,
/// keyed by its <c>(version, index)</c>.
/// </summary>
public sealed class MediaStream
{
    private const int CodecMaxLength = 80;
    private const int LanguageMaxLength = 40;

    public Guid Id { get; init; }

    public Guid MediaVersionId { get; init; }

    public int StreamIndex { get; init; }

    public MediaStreamType Type { get; init; }

    public string? Codec { get; init; }

    public string? Language { get; init; }

    public int? Channels { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? BitDepth { get; init; }

    public VideoRangeType? VideoRangeType { get; init; }

    public bool IsDefault { get; init; }

    public bool IsForced { get; init; }

    /// <summary>True for a sidecar/external track (added later by Subtitles); internal streams are false.</summary>
    public bool IsExternal { get; init; }

    /// <summary>Builds an external (sidecar) subtitle stream — added when Subtitles lands a file for the asset.</summary>
    public static MediaStream ExternalSubtitle(Guid mediaVersionId, int streamIndex, string language, bool forced) => new()
    {
        Id = Kernel.Identifiers.Uuid7.New(),
        MediaVersionId = mediaVersionId,
        StreamIndex = streamIndex,
        Type = MediaStreamType.Subtitle,
        Codec = "external",
        Language = Text.Truncate(language, LanguageMaxLength),
        IsForced = forced,
        IsExternal = true,
    };

    public static MediaStream FromInput(Guid mediaVersionId, MediaStreamInput input) => new()
    {
        Id = Kernel.Identifiers.Uuid7.New(),
        MediaVersionId = mediaVersionId,
        StreamIndex = input.StreamIndex,
        Type = input.Type,
        Codec = Text.Truncate(input.Codec, CodecMaxLength),
        Language = Text.Truncate(input.Language, LanguageMaxLength),
        Channels = input.Channels,
        Width = input.Width,
        Height = input.Height,
        BitDepth = input.BitDepth,
        VideoRangeType = input.VideoRangeType,
        IsDefault = input.IsDefault,
        IsForced = input.IsForced,
        IsExternal = false,
    };
}
