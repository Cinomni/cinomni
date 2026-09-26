namespace Cinomni.Playback.Encoding;

/// <summary>
/// Configuration for the FFmpeg encoder. The binary path is fixed by the admin (a packaged, trusted
/// build), never from the request. Codec values are constrained to a closed whitelist so
/// no free value reaches an argument.
/// </summary>
public sealed class FfmpegEncoderOptions
{
    public string BinaryPath { get; set; } = "ffmpeg";

    /// <summary>The ffprobe that ships with <see cref="BinaryPath"/>, for the few facts Playback reads itself.</summary>
    public string ProbeBinaryPath { get; set; } = "ffprobe";

    /// <summary>HLS segment length in seconds.</summary>
    public int SegmentSeconds { get; set; } = 6;

    /// <summary>Allowed video codec arguments (closed whitelist).</summary>
    public IReadOnlySet<string> AllowedVideoCodecs { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "libx264", "libx265", "copy",
    };

    /// <summary>Allowed audio codec arguments (closed whitelist).</summary>
    public IReadOnlySet<string> AllowedAudioCodecs { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "aac", "copy",
    };
}
