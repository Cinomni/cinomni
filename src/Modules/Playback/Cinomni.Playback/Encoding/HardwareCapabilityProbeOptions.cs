namespace Cinomni.Playback.Encoding;

/// <summary>
/// Configuration for the hardware capability probe. The FFmpeg binary path mirrors
/// <see cref="FfmpegEncoderOptions.BinaryPath"/> (same trusted, packaged build); the
/// device path is the VAAPI/QSV render node the container would need passed through
/// (<c>/dev/dri/...</c>), and the NVIDIA path is the control node nvidia-container-toolkit creates
/// only when the container was actually granted a GPU.
/// </summary>
public sealed class HardwareCapabilityProbeOptions
{
    public string BinaryPath { get; set; } = "ffmpeg";

    /// <summary>The VAAPI/QSV render node to check for. Absent means neither backend is available.</summary>
    public string DevicePath { get; set; } = "/dev/dri/renderD128";

    /// <summary>
    /// The node whose presence says an NVIDIA GPU was granted to this container. FFmpeg builds carry
    /// <c>h264_nvenc</c> whether or not there is a GPU, so the encoder list alone proves nothing.
    /// </summary>
    public string NvidiaDevicePath { get; set; } = "/dev/nvidiactl";

    /// <summary>Hard cap on how long one probe invocation (<c>-hwaccels</c> or <c>-encoders</c>) may run.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
