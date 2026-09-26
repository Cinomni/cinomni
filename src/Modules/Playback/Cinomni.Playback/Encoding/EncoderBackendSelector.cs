using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// The chosen encoder backend, whether it also decodes the source in hardware, plus why —
/// carried onto the explainable playback plan.
/// </summary>
public sealed record EncoderBackendSelection(
    EncoderBackend Backend, IReadOnlyList<string> AccelerationReasons, bool DecodeAccelerated);

/// <summary>
/// Picks which encoder backend a transcode should use, and whether that backend can also decode the
/// source's video codec in hardware. A pure function of the host's detected
/// <see cref="HardwareCapabilities"/> (see <see cref="IHardwareCapabilityProbe"/>), the operator's
/// <see cref="TranscodingOptions"/>, the output codec and the source's video codec — no FFmpeg, no
/// I/O — so it is unit-tested by the selection it produces, on the same precedent as <c>IPlaybackPlanner</c>.
/// </summary>
public interface IEncoderBackendSelector
{
    /// <param name="sourceVideoCodec">
    /// The asset's video codec (e.g. <c>h264</c>), or <c>null</c> when unknown — decode is never
    /// reported as accelerated in that case, since there is nothing to check it against.
    /// </param>
    EncoderBackendSelection Select(
        HardwareCapabilities capabilities,
        string? sourceVideoCodec,
        TranscodingOptions? options = null,
        VideoOutputCodec codec = VideoOutputCodec.H264);
}

/// <summary>
/// Takes the operator's preferred backend when it was detected and encodes the codec; otherwise the
/// first detected one in a fixed order — VAAPI (the broadest driver support on Linux: Intel and AMD
/// both expose it through Mesa), then NVENC, then AMF (AMD on Windows), then QSV, which FFmpeg itself
/// recommends VAAPI over on Linux where both are present. Falls back to software whenever no detected
/// backend can encode the codec.
/// </summary>
public sealed class EncoderBackendSelector : IEncoderBackendSelector
{
    private static readonly IReadOnlyList<EncoderBackend> PreferenceOrder =
        [EncoderBackend.Vaapi, EncoderBackend.Nvenc, EncoderBackend.Amf, EncoderBackend.Qsv];

    public EncoderBackendSelection Select(
        HardwareCapabilities capabilities,
        string? sourceVideoCodec,
        TranscodingOptions? options = null,
        VideoOutputCodec codec = VideoOutputCodec.H264)
    {
        options ??= TranscodingOptions.Default;
        var reasons = new List<string>();
        var preferred = options.PreferredBackend;

        var order = PreferenceOrder.ToList();
        if (preferred is { } wanted)
        {
            order.Remove(wanted);
            order.Insert(0, wanted);
            if (!capabilities.CanEncode(wanted, codec))
            {
                reasons.Add($"the preferred backend '{wanted}' was not detected on this host (or cannot encode {codec}); using the next one available");
            }
        }

        foreach (var backend in order)
        {
            if (!capabilities.CanEncode(backend, codec))
            {
                continue;
            }

            reasons.Add(backend == preferred
                ? $"hardware backend '{backend}' is the operator's choice and was detected on this host"
                : $"hardware backend '{backend}' was detected on this host and is used for this transcode");

            var decodeCapable = sourceVideoCodec is not null && capabilities.DecodeCapableCodecs.Any(
                c => c.Backend == backend && string.Equals(c.Codec, sourceVideoCodec, StringComparison.OrdinalIgnoreCase));
            var decodeAccelerated = decodeCapable && options.HardwareDecoding;

            reasons.Add(decodeAccelerated
                ? $"source codec '{sourceVideoCodec}' is also decoded on '{backend}', avoiding a software decode"
                : decodeCapable
                    ? "hardware decoding is turned off in settings; decode stays in software"
                    : $"source codec '{sourceVideoCodec ?? "unknown"}' has no hardware decoder detected on '{backend}'; decode stays in software");

            return new EncoderBackendSelection(backend, reasons, decodeAccelerated);
        }

        reasons.Add(codec == VideoOutputCodec.Hevc
            ? "no hardware backend on this host encodes HEVC; transcoding uses software x265"
            : "no hardware encoder backend was detected on this host; transcoding uses software x264");
        return new EncoderBackendSelection(EncoderBackend.Software, reasons, DecodeAccelerated: false);
    }
}
