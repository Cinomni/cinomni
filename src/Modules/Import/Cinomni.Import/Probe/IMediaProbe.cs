using Cinomni.Import.Contracts;

namespace Cinomni.Import.Probe;

/// <summary>
/// Port to the media analyser (ffprobe). The production adapter
/// (<see cref="FfprobeMediaProbe"/>) runs ffprobe as an isolated child process with an argv list and
/// a timeout, so a file crafted to exploit the decoder cannot compromise the backend.
/// Tests substitute a scripted fake. A probe that fails returns
/// <see cref="MediaInfo.Empty"/> rather than throwing: the asset still registers and can be
/// re-probed later (the FSM's probe-failure rule).
/// </summary>
public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken = default);
}
