namespace Cinomni.Import.Probe;

/// <summary>
/// Configuration for the ffprobe adapter. The binary path is fixed by the admin (a packaged, trusted
/// build), never discovered or downloaded at runtime. The timeout bounds every probe
/// (an isolated process that cannot hang the backend).
/// </summary>
public sealed class FfprobeOptions
{
    /// <summary>Path to the trusted ffprobe binary (packaged with the deployment).</summary>
    public string BinaryPath { get; set; } = "ffprobe";

    /// <summary>Hard cap on how long a single probe may run before it is killed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}
