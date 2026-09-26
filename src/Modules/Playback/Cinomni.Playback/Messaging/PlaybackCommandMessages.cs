using Cinomni.Kernel.Messaging;

namespace Cinomni.Playback.Messaging;

/// <summary>Stable registered names of the Playback commands.</summary>
public static class PlaybackCommandNames
{
    public const string PurgeSessions = "playback.purge-sessions";
}

/// <summary>
/// Ages finished playback sessions out with their transcode jobs. Parameterless — the scheduler
/// constructs it and the window is deployment configuration.
/// </summary>
public sealed record PurgePlaybackSessionsCommand : ICommand;
