namespace Cinomni.Playback.Encoding;

/// <summary>
/// Whether the planner may choose a detected hardware encoder backend at all — an operator's escape
/// hatch. Hardware stays auto-selected by default (<see cref="EncoderBackendSelector"/> only ever
/// picks a backend the startup probe actually found); this lets an operator force every transcode to
/// software without touching the container or restarting, e.g. to take a misbehaving driver out of
/// rotation. Deliberately its own small options type rather than a property on <c>PlaybackOptions</c>:
/// the latter is only ever registered by <c>AddPlaybackAdapters</c>, which a test host can skip, while
/// <see cref="Cinomni.Playback.Planning.PlaybackPlanner"/> — this setting's one consumer — is
/// registered unconditionally by <c>AddPlaybackModule</c>.
/// </summary>
public sealed class HardwareTranscodingOptions
{
    internal const string EnabledKey = "playback.hardwareTranscodingEnabled";

    public bool Enabled { get; set; } = true;
}
