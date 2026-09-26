using System.Globalization;
using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// Builds FFmpeg's argv for a transcode, and for the probe's hardware tests, as a pure function — no
/// process, no disk — so every pipeline shape is unit-tested by the argv it produces.
/// <para>
/// Every value that reaches the argv is a path the caller already confined, an integer, or text picked
/// from a closed switch over an enum. There is no free string from a request or a setting.
/// </para>
/// <para>
/// A frame travels one of two ways. It stays on the device from decode to encode (the <b>device</b>
/// pipeline) only when nothing needs it in system memory; tone mapping and burning in a subtitle both
/// do, so either one decodes in software (or downloads, for backends that decode to system memory
/// anyway) and filters there, then hands the result to the encoder — uploading it first for VAAPI.
/// </para>
/// </summary>
public static class FfmpegCommand
{
    public const string ManifestName = "manifest.m3u8";
    public const string InitSegmentName = "init.mp4";

    /// <summary>The FFmpeg encoder for a backend and an output codec — a closed switch, never a string from outside.</summary>
    public static string EncoderName(EncoderBackend backend, VideoOutputCodec codec) => (backend, codec) switch
    {
        (EncoderBackend.Vaapi, VideoOutputCodec.Hevc) => "hevc_vaapi",
        (EncoderBackend.Vaapi, _) => "h264_vaapi",
        (EncoderBackend.Qsv, VideoOutputCodec.Hevc) => "hevc_qsv",
        (EncoderBackend.Qsv, _) => "h264_qsv",
        (EncoderBackend.Nvenc, VideoOutputCodec.Hevc) => "hevc_nvenc",
        (EncoderBackend.Nvenc, _) => "h264_nvenc",
        (EncoderBackend.Amf, VideoOutputCodec.Hevc) => "hevc_amf",
        (EncoderBackend.Amf, _) => "h264_amf",
        (_, VideoOutputCodec.Hevc) => "libx265",
        _ => "libx264",
    };

    /// <summary>
    /// Whether a transcode keeps its frames on the device from decode to encode. Only when the plan asked
    /// for hardware decode, nothing needs the frame in system memory, and the backend has a scaler of its
    /// own: QSV and AMF are decoded to system memory in this pipeline, so they filter in software anyway.
    /// </summary>
    public static bool UsesDevicePipeline(TranscodeRequest request, EncoderBackend backend) =>
        request.AccelerateDecode
        && !request.ToneMap
        && request.BurnInSubtitleIndex is null
        && backend is EncoderBackend.Vaapi or EncoderBackend.Nvenc;

    /// <summary>The full argv of a transcode or remux to HLS, run by <paramref name="backend"/> (the planned one, or software on the retry).</summary>
    public static IReadOnlyList<string> Transcode(
        TranscodeRequest request, EncoderBackend backend, int segmentSeconds, string vaapiDevicePath)
    {
        var copy = request.VideoCodec == "copy";
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error" };
        var device = !copy && UsesDevicePipeline(request, backend);

        if (!copy)
        {
            args.AddRange(InputOptions(backend, device, request.AccelerateDecode, vaapiDevicePath));
        }

        args.Add("-nostdin");
        if (request.StartSeconds > 0)
        {
            // An input option: FFmpeg seeks the file before decoding, and the output starts at zero.
            args.Add("-ss");
            args.Add(request.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(request.InputPath);

        AddVideo(args, request, backend, copy, device, segmentSeconds);
        AddAudio(args, request);
        AddHls(args, request, segmentSeconds);
        return args;
    }

    /// <summary>
    /// The input-side options: the device, and hardware decode when the plan uses it. Decode stays on the
    /// device even when the frame has to be filtered in system memory — FFmpeg downloads each decoded
    /// frame — because decoding is the expensive half of a 4K HEVC source.
    /// </summary>
    private static IEnumerable<string> InputOptions(
        EncoderBackend backend, bool devicePipeline, bool hardwareDecode, string vaapiDevicePath)
    {
        var args = new List<string>();
        switch (backend)
        {
            case EncoderBackend.Vaapi:
                // One named device serves the decoder, the filters that upload to it, and the encoder.
                args.AddRange(["-init_hw_device", $"vaapi=va:{vaapiDevicePath}", "-filter_hw_device", "va"]);
                if (hardwareDecode)
                {
                    args.AddRange(["-hwaccel", "vaapi", "-hwaccel_device", "va"]);
                }

                if (devicePipeline)
                {
                    args.AddRange(["-hwaccel_output_format", "vaapi"]);
                }

                break;
            case EncoderBackend.Nvenc when hardwareDecode:
                args.AddRange(["-hwaccel", "cuda"]);
                if (devicePipeline)
                {
                    args.AddRange(["-hwaccel_output_format", "cuda"]);
                }

                break;
            case EncoderBackend.Qsv when hardwareDecode:
                args.AddRange(["-hwaccel", "qsv"]);
                break;
            case EncoderBackend.Amf when hardwareDecode:
                args.AddRange(["-hwaccel", "d3d11va"]);
                break;
        }

        return args;
    }

    private static void AddVideo(
        List<string> args, TranscodeRequest request, EncoderBackend backend, bool copy, bool devicePipeline, int segmentSeconds)
    {
        if (copy)
        {
            args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
            if (request.Fmp4Segments)
            {
                args.AddRange(["-tag:v", "hvc1"]);
            }

            return;
        }

        var chain = devicePipeline
            ? DeviceChain(request, backend)
            : SoftwareChain(request, backend);

        if (request.BurnInSubtitleIndex is { } subtitle)
        {
            // The subtitle is drawn over the picture before anything else, at the source's own size, so it
            // scales and tone-maps with the frame it belongs to.
            var index = subtitle.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-filter_complex", $"[0:v:0][0:{index}]overlay=eof_action=pass,{chain}[v]", "-map", "[v]"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-vf", chain]);
        }

        var codec = request.OutputCodec;
        args.AddRange(["-c:v", EncoderName(backend, codec)]);
        args.AddRange(RateControl(backend, codec, request.Tuning, request.MaxBitrateKbps));
        if (codec == VideoOutputCodec.Hevc)
        {
            // Browsers only play HEVC in MP4 tagged hvc1, not hev1.
            args.AddRange(["-tag:v", "hvc1"]);
        }

        // A keyframe at every segment boundary, so every segment starts cleanly and a seek lands on one.
        args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{segmentSeconds.ToString(CultureInfo.InvariantCulture)})"]);
    }

    /// <summary>Filters for frames that never leave the device: its own scaler, which also converts 10-bit to 8-bit.</summary>
    private static string DeviceChain(TranscodeRequest request, EncoderBackend backend)
    {
        var scale = ScaleOptions(request.MaxWidth, request.MaxHeight);
        return backend == EncoderBackend.Nvenc
            ? $"scale_cuda={(scale is null ? string.Empty : scale + ":")}format=yuv420p"
            : $"scale_vaapi={(scale is null ? string.Empty : scale + ":")}format=nv12";
    }

    /// <summary>
    /// Filters for frames in system memory: scaling, then tone mapping, then the encoder's pixel format.
    /// Scaling comes first because tone mapping works on 32-bit float planes: on a 4K source brought to
    /// 1080p it touches a quarter of the pixels, which measured twice as fast end to end.
    /// </summary>
    private static string SoftwareChain(TranscodeRequest request, EncoderBackend backend)
    {
        var filters = new List<string>();
        if (ScaleOptions(request.MaxWidth, request.MaxHeight) is { } scale)
        {
            filters.Add($"scale={scale}");
        }

        if (request.ToneMap)
        {
            filters.Add(ToneMapChain(request.Tuning.ToneMapAlgorithm));
        }

        filters.Add(backend is EncoderBackend.Vaapi or EncoderBackend.Qsv or EncoderBackend.Amf ? "format=nv12" : "format=yuv420p");
        if (backend == EncoderBackend.Vaapi)
        {
            filters.Add("hwupload");
        }

        return string.Join(',', filters);
    }

    /// <summary>
    /// HDR (PQ or HLG) to SDR BT.709: linearise, bring the highlights down with the chosen curve, and
    /// re-encode to BT.709. Done in software, which is why it takes the frame off the device.
    /// </summary>
    public static string ToneMapChain(ToneMapAlgorithm algorithm)
    {
        var curve = algorithm switch
        {
            ToneMapAlgorithm.Reinhard => "reinhard",
            ToneMapAlgorithm.Mobius => "mobius",
            ToneMapAlgorithm.Bt2390 => "bt2390",
            _ => "hable",
        };
        return "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
            + $"tonemap=tonemap={curve}:desat=0,zscale=t=bt709:m=bt709:r=tv";
    }

    /// <summary>
    /// The options that shrink the picture to fit a box and never enlarge it: each side is the smaller of
    /// the source's and the box's, the aspect ratio is kept by shrinking into that, and the result stays
    /// even as H.264 needs. Null when nothing caps the size. The same options serve every scaler FFmpeg
    /// has (<c>scale</c>, <c>scale_vaapi</c>, <c>scale_cuda</c>).
    /// </summary>
    public static string? ScaleOptions(int? maxWidth, int? maxHeight)
    {
        if (maxWidth is null && maxHeight is null)
        {
            return null;
        }

        var width = maxWidth is { } w ? $"'min(iw,{w.ToString(CultureInfo.InvariantCulture)})'" : "iw";
        var height = maxHeight is { } h ? $"'min(ih,{h.ToString(CultureInfo.InvariantCulture)})'" : "ih";
        return $"w={width}:h={height}:force_original_aspect_ratio=decrease:force_divisible_by=2";
    }

    /// <summary>
    /// Quality and speed in each encoder's own terms: a constant-quality target when nothing caps the
    /// bitrate, a capped variable rate when something does. Every value is an integer or a word from a
    /// closed switch.
    /// </summary>
    public static IReadOnlyList<string> RateControl(
        EncoderBackend backend, VideoOutputCodec codec, EncodeTuning tuning, int? maxBitrateKbps)
    {
        var quality = Math.Clamp(tuning.Quality, 15, 40).ToString(CultureInfo.InvariantCulture);
        var capped = maxBitrateKbps is > 0;
        var rate = capped ? maxBitrateKbps!.Value.ToString(CultureInfo.InvariantCulture) + "k" : string.Empty;
        var buffer = capped ? (maxBitrateKbps!.Value * 2).ToString(CultureInfo.InvariantCulture) + "k" : string.Empty;
        var args = new List<string>();

        switch (backend)
        {
            case EncoderBackend.Nvenc:
                args.AddRange(["-preset", tuning.Preset switch
                {
                    EncoderPreset.Fastest => "p1",
                    EncoderPreset.Fast => "p3",
                    EncoderPreset.Balanced => "p4",
                    _ => "p6",
                }]);
                args.AddRange(["-rc", "vbr", "-cq", quality]);
                args.AddRange(capped ? ["-b:v", rate, "-maxrate", rate, "-bufsize", buffer] : ["-b:v", "0"]);
                break;
            case EncoderBackend.Qsv:
                args.AddRange(["-preset", tuning.Preset switch
                {
                    EncoderPreset.Fastest => "veryfast",
                    EncoderPreset.Fast => "faster",
                    EncoderPreset.Balanced => "medium",
                    _ => "slower",
                }]);
                args.AddRange(capped ? ["-b:v", rate, "-maxrate", rate, "-bufsize", buffer] : ["-global_quality", quality]);
                break;
            case EncoderBackend.Vaapi:
                args.AddRange(capped
                    ? ["-rc_mode", "VBR", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer]
                    : ["-rc_mode", "CQP", "-qp", quality]);
                break;
            case EncoderBackend.Amf:
                args.AddRange(["-quality", tuning.Preset switch
                {
                    EncoderPreset.Fastest or EncoderPreset.Fast => "speed",
                    EncoderPreset.Balanced => "balanced",
                    _ => "quality",
                }]);
                args.AddRange(capped
                    ? ["-rc", "vbr_peak", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer]
                    : ["-rc", "cqp", "-qp_i", quality, "-qp_p", quality]);
                break;
            default:
                args.AddRange(["-preset", tuning.Preset switch
                {
                    EncoderPreset.Fastest => "ultrafast",
                    EncoderPreset.Fast => "veryfast",
                    EncoderPreset.Balanced => "fast",
                    _ => "medium",
                }]);
                args.AddRange(["-crf", quality]);
                if (capped)
                {
                    args.AddRange(["-maxrate", rate, "-bufsize", buffer]);
                }

                if (tuning.Threads > 0)
                {
                    args.AddRange(["-threads", tuning.Threads.ToString(CultureInfo.InvariantCulture)]);
                }

                if (codec == VideoOutputCodec.Hevc)
                {
                    args.AddRange(["-x265-params", "log-level=error"]);
                }

                break;
        }

        return args;
    }

    private static void AddAudio(List<string> args, TranscodeRequest request)
    {
        if (request.AudioStreamIndex is { } audioIndex)
        {
            args.AddRange(["-map", "0:" + audioIndex.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-c:a", request.AudioCodec]);
        if (request.AudioCodec == "copy")
        {
            return;
        }

        args.AddRange(["-b:a", Math.Clamp(request.Tuning.AudioBitrateKbps, 64, 640).ToString(CultureInfo.InvariantCulture) + "k"]);
        if (request.AudioChannels is { } channels and > 0)
        {
            args.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        }
    }

    private static void AddHls(List<string> args, TranscodeRequest request, int segmentSeconds)
    {
        args.AddRange(["-f", "hls", "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture), "-hls_playlist_type", "event"]);
        if (request.Fmp4Segments)
        {
            args.AddRange([
                "-hls_segment_type", "fmp4",
                "-hls_fmp4_init_filename", InitSegmentName,
                "-hls_segment_filename", Path.Combine(request.OutputDirectory, "seg_%03d.m4s"),
            ]);
        }
        else
        {
            args.AddRange(["-hls_segment_filename", Path.Combine(request.OutputDirectory, "seg_%03d.ts")]);
        }

        args.Add(Path.Combine(request.OutputDirectory, ManifestName));
    }

    /// <summary>
    /// A probe test that encodes a few frames of a generated picture on <paramref name="backend"/> and
    /// throws them away — proof the encoder, its driver and the device all work, which a listed encoder
    /// name is not.
    /// </summary>
    public static IReadOnlyList<string> EncodeTest(EncoderBackend backend, VideoOutputCodec codec, string vaapiDevicePath)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error" };
        if (backend == EncoderBackend.Vaapi)
        {
            args.AddRange(["-vaapi_device", vaapiDevicePath]);
        }

        args.AddRange(["-nostdin", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.4"]);
        args.AddRange(["-vf", backend switch
        {
            EncoderBackend.Vaapi => "format=nv12,hwupload",
            EncoderBackend.Qsv or EncoderBackend.Amf => "format=nv12",
            _ => "format=yuv420p",
        }]);
        args.AddRange(["-c:v", EncoderName(backend, codec), "-frames:v", "5", "-f", "null", "-"]);
        return args;
    }

    /// <summary>
    /// A probe test that decodes <paramref name="samplePath"/> on the device and keeps the frame there
    /// through the device's own filter, so a silent fall back to software decode fails the test instead of
    /// passing it. Null for a backend with no such pipeline here.
    /// </summary>
    public static IReadOnlyList<string>? DecodeTest(
        EncoderBackend backend, VideoOutputCodec encodeWith, string samplePath, string vaapiDevicePath)
    {
        string[] input;
        string filter;
        switch (backend)
        {
            case EncoderBackend.Vaapi:
                input = ["-hwaccel", "vaapi", "-hwaccel_device", vaapiDevicePath, "-hwaccel_output_format", "vaapi"];
                filter = "scale_vaapi=format=nv12";
                break;
            case EncoderBackend.Nvenc:
                input = ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"];
                filter = "scale_cuda=format=yuv420p";
                break;
            case EncoderBackend.Qsv:
                input = ["-hwaccel", "qsv", "-hwaccel_output_format", "qsv"];
                filter = "vpp_qsv=format=nv12";
                break;
            case EncoderBackend.Amf:
                input = ["-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11"];
                filter = "hwdownload,format=nv12";
                break;
            default:
                return null;
        }

        return
        [
            "-hide_banner", "-nostats", "-loglevel", "error", .. input, "-nostdin", "-i", samplePath,
            "-vf", filter, "-c:v", EncoderName(backend, encodeWith), "-frames:v", "5", "-f", "null", "-",
        ];
    }

    /// <summary>A few frames of a generated picture encoded in software — the clip a decode test decodes.</summary>
    public static IReadOnlyList<string> Sample(VideoOutputCodec codec, string samplePath) =>
    [
        "-hide_banner", "-nostats", "-loglevel", "error", "-nostdin", "-y",
        "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=0.4",
        "-c:v", codec == VideoOutputCodec.Hevc ? "libx265" : "libx264",
        .. codec == VideoOutputCodec.Hevc ? ["-x265-params", "log-level=error"] : Array.Empty<string>(),
        "-pix_fmt", "yuv420p", samplePath,
    ];
}
