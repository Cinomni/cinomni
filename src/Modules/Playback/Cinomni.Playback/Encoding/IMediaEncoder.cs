using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// What to transcode/remux to HLS: the input file, the output directory, the target codecs (or copy),
/// which encoder backend to prefer for an actual video encode, whether that backend should also
/// decode the source in hardware, and the selected tracks. Codec names are validated against a
/// closed whitelist by the adapter; <see cref="Backend"/> and <see cref="AccelerateDecode"/> only
/// ever come from the process's own <c>HardwareCapabilitiesCache</c> by way of the playback plan,
/// never from a caller-supplied value, so the adapter can pick its FFmpeg codec name and hwaccel argv
/// from a closed switch over the enum rather than trusting a value.
/// <para>
/// The ceilings (<see cref="MaxWidth"/>, <see cref="MaxHeight"/>, <see cref="MaxBitrateKbps"/>) come
/// from the plan — the client's screen or a rung of the server's own quality ladder — and only apply to
/// an actual encode; <see cref="StartSeconds"/> starts the output that far into the file.
/// </para>
/// <para>
/// The rest is also the plan's: whether HDR is brought to SDR (<see cref="ToneMap"/>), which picture
/// subtitle is burned in, how many audio channels are kept, whether the segments are fragmented MP4
/// (HEVC in a browser needs it), and the operator's encoder tuning at the time the session opened.
/// </para>
/// </summary>
public sealed record TranscodeRequest(
    string InputPath,
    string OutputDirectory,
    string VideoCodec,
    string AudioCodec,
    EncoderBackend Backend,
    bool AccelerateDecode,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex,
    int? MaxWidth = null,
    int? MaxHeight = null,
    int? MaxBitrateKbps = null,
    double StartSeconds = 0)
{
    public bool ToneMap { get; init; }

    /// <summary>The absolute index of a picture subtitle stream to burn into the video; null for none.</summary>
    public int? BurnInSubtitleIndex { get; init; }

    /// <summary>The channel count to fold the audio down to; null keeps the source's.</summary>
    public int? AudioChannels { get; init; }

    public bool Fmp4Segments { get; init; }

    public EncodeTuning Tuning { get; init; } = EncodeTuning.Default;

    /// <summary>The codec an encode produces — read off the whitelisted software codec name.</summary>
    public VideoOutputCodec OutputCodec => VideoCodec == "libx265" ? VideoOutputCodec.Hevc : VideoOutputCodec.H264;
}

/// <summary>The operator's encoder tuning, captured when the session opened (see <see cref="TranscodingOptions"/>).</summary>
public sealed record EncodeTuning(
    EncoderPreset Preset,
    int Quality,
    int Threads,
    ToneMapAlgorithm ToneMapAlgorithm,
    int AudioBitrateKbps)
{
    public static EncodeTuning Default { get; } = From(TranscodingOptions.Default);

    public static EncodeTuning From(TranscodingOptions options) =>
        new(options.Preset, options.Quality, options.Threads, options.ToneMapAlgorithm, options.AudioBitrateKbps);
}

/// <summary>
/// The result of starting a transcode: where the HLS manifest lives, which backend actually ran it,
/// and the process that is still producing it. <see cref="BackendUsed"/> differs from the request's
/// <see cref="TranscodeRequest.Backend"/> exactly when the planned hardware backend failed and the
/// adapter fell back to software (a bounded, one-time retry) — the caller persists that fact rather
/// than silently accepting a plan that no longer describes what happened.
/// <para>
/// <see cref="Process"/> belongs to the caller from this point on: nothing else will ever stop it.
/// </para>
/// </summary>
public sealed record TranscodeOutput(string ManifestPath, EncoderBackend BackendUsed, IRunningTranscode Process);

/// <summary>
/// A transcode the encoder started and handed over. It runs until it finishes the file on its own or
/// is disposed; disposing kills it (and anything it spawned) and is safe to repeat.
/// </summary>
public interface IRunningTranscode : IAsyncDisposable
{
    /// <summary>Whether the process is gone, on its own or because it was disposed.</summary>
    bool HasExited { get; }

    /// <summary>
    /// The operating system's id for the process, and when it started — recorded on the job so that a
    /// later run can stop this process, and only this one, if the host dies without stopping it. Null
    /// when the operating system would not say.
    /// </summary>
    int? ProcessId { get; }

    DateTimeOffset? StartedAt { get; }

    /// <summary>The exit code once the process is gone and the code could be read; otherwise null.</summary>
    int? ExitCode { get; }

    /// <summary>
    /// The last lines the process wrote to stderr, bounded. It can quote the media path, so it belongs
    /// on the transcode job row and nowhere else — never in a log line, a response, an event or a metric.
    /// </summary>
    string Diagnostics { get; }
}

/// <summary>
/// Port to FFmpeg for HLS delivery. The production adapter
/// (<see cref="FfmpegMediaEncoder"/>) spawns FFmpeg as an isolated process with an <b>argv</b> list
/// (never a shell), so its failure never takes down the server and no value can inject a flag. Tests
/// substitute a fake. Not run by the module's unit tests (FFmpeg is a deployment concern, like the
/// ffprobe binary and the libtorrent sidecar); the planner is tested without it.
/// <para>
/// A start either hands back a manifest and the running process, or throws — an FFmpeg that exits
/// without ever writing a manifest is a failed start, not a session with nothing to serve. Whatever
/// the outcome, no process is left running that the caller was not handed.
/// </para>
/// </summary>
public interface IMediaEncoder
{
    Task<TranscodeOutput> StartHlsAsync(TranscodeRequest request, CancellationToken cancellationToken = default);
}
