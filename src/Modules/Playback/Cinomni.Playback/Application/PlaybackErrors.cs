namespace Cinomni.Playback.Application;

/// <summary>The error codes a playback request can fail with — the <c>error</c> half of the envelope.</summary>
public static class PlaybackErrors
{
    /// <summary>No asset, no playable version, or one this account may not see — deliberately indistinguishable.</summary>
    public const string AssetNotFound = "playback.asset_not_found";

    /// <summary>FFmpeg could not produce a stream; the reason is on the transcode job.</summary>
    public const string TranscodeFailed = "playback.transcode_failed";

    /// <summary>The node or the account is already running as many transcodes as it is allowed.</summary>
    public const string TranscodeLimit = "playback.transcode_limit";

    /// <summary>The request named a track or a quality the file does not offer.</summary>
    public const string InvalidPreference = "playback.invalid_preference";
}
