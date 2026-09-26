using System.Globalization;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// FFmpeg exited without ever writing a manifest. The two halves of the explanation travel apart on
/// purpose: <see cref="Exception.Message"/> names only the exit code, so it is safe for a log line, a
/// trace and an integration event; <see cref="Diagnostics"/> is the end of FFmpeg's stderr, which can
/// quote the media path, and belongs on the transcode job row alone.
/// </summary>
public sealed class TranscodeStartFailedException(int? exitCode, string diagnostics)
    : Exception(Summarize(exitCode))
{
    public int? ExitCode { get; } = exitCode;

    public string Diagnostics { get; } = diagnostics;

    private static string Summarize(int? exitCode) =>
        $"FFmpeg exited with code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} before writing a manifest.";
}
