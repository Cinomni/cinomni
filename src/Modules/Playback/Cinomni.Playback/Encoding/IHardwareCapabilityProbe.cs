using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// A source video codec (lower-case, e.g. <c>h264</c>) a given <see cref="EncoderBackend"/> can also
/// decode in hardware — decode and encode are separate driver/silicon paths on the same device (most
/// visibly on NVIDIA, where NVENC and NVDEC are distinct), so this is never inferred from
/// <see cref="HardwareCapabilities.AvailableBackends"/>.
/// </summary>
public sealed record DecodeCapability(EncoderBackend Backend, string Codec);

/// <summary>An output codec a backend was seen to encode.</summary>
public sealed record EncodeCapability(EncoderBackend Backend, VideoOutputCodec Codec);

/// <summary>What one probe test tried, and whether the hardware actually did it.</summary>
public enum ProbeTestKind
{
    Encode = 1,
    Decode = 2,
}

/// <summary>
/// One test the probe ran against real hardware: a short encode of a generated picture, or a decode of
/// a generated clip through the device's own pipeline. <see cref="Failure"/> is the end of FFmpeg's
/// complaint, with no paths in it.
/// </summary>
public sealed record ProbeTestResult(EncoderBackend Backend, ProbeTestKind Kind, string Codec, bool Passed, string? Failure);

/// <summary>What the probe saw, for the operator: the FFmpeg build, the platform, and every test it ran.</summary>
public sealed record HardwareProbeReport(
    string FfmpegVersion,
    string Platform,
    DateTimeOffset ProbedAt,
    IReadOnlyList<string> Hwaccels,
    IReadOnlyList<ProbeTestResult> Tests);

/// <summary>
/// Which hardware encoder backends this host can actually use, right now, and which source codecs
/// each of those backends can also decode. <see cref="DecodeCapableCodecs"/> defaults to empty so
/// every existing single-argument construction (a backend list with no decode information) still
/// compiles and behaves exactly as before — decode simply never accelerates.
/// <para>
/// The rest describes the FFmpeg build: which output codecs each backend encodes (empty means H.264 on
/// every available backend, the shape of a result from before HEVC was probed), whether libx265 is
/// there, and whether the filters tone mapping and subtitle burn-in need are.
/// </para>
/// </summary>
public sealed record HardwareCapabilities(
    IReadOnlyList<EncoderBackend> AvailableBackends,
    IReadOnlyList<DecodeCapability> DecodeCapableCodecs)
{
    public HardwareCapabilities(IReadOnlyList<EncoderBackend> availableBackends)
        : this(availableBackends, [])
    {
    }

    public IReadOnlyList<EncodeCapability> EncodableCodecs { get; init; } = [];

    public bool SoftwareHevc { get; init; }

    public bool ToneMapping { get; init; }

    public bool SubtitleOverlay { get; init; }

    public HardwareProbeReport? Report { get; init; }

    /// <summary>Whether <paramref name="backend"/> can produce <paramref name="codec"/> on this host.</summary>
    public bool CanEncode(EncoderBackend backend, VideoOutputCodec codec)
    {
        if (backend == EncoderBackend.Software)
        {
            return codec == VideoOutputCodec.H264 || SoftwareHevc;
        }

        if (!AvailableBackends.Contains(backend))
        {
            return false;
        }

        return EncodableCodecs.Count == 0
            ? codec == VideoOutputCodec.H264
            : EncodableCodecs.Contains(new EncodeCapability(backend, codec));
    }
}

/// <summary>
/// Port to hardware-encoder detection: run at startup and again when an operator asks (probing per
/// playback request would be wasteful and racy against a device that doesn't change mid-process), with
/// the result cached by whoever registers this. The production adapter asks FFmpeg's own CLI and then
/// runs real test encodes and decodes; tests substitute a fake with a canned result.
/// </summary>
public interface IHardwareCapabilityProbe
{
    Task<HardwareCapabilities> ProbeAsync(CancellationToken cancellationToken = default);
}
