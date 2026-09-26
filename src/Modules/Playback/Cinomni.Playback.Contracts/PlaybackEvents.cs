using Cinomni.Kernel.Messaging;

namespace Cinomni.Playback.Contracts;

/// <summary>Stable registered names of the Playback integration events.</summary>
public static class PlaybackEventNames
{
    public const string PlaybackStarted = "playback.started";
    public const string PlaybackProgressUpdated = "playback.progress-updated";
    public const string PlaybackCompleted = "playback.completed";
    public const string TranscodeStarted = "playback.transcode-started";
    public const string TranscodeFailed = "playback.transcode-failed";
}

/// <summary>A playback session began delivering content. Consumed by Notifications. Keyed by the session.</summary>
public sealed record PlaybackStarted(
    Guid SessionId,
    Guid UserId,
    Guid AssetId,
    string Method) : DomainEvent
{
    public override string IdempotencyKey => $"playback-started:{SessionId}";
}

/// <summary>
/// The session's position advanced (best-effort, high volume). Keyed by session+sequence so each
/// tick is distinct; not every tick needs durable delivery.
/// </summary>
public sealed record PlaybackProgressUpdated(
    Guid SessionId,
    long PositionTicks,
    bool IsPaused,
    int Seq) : DomainEvent
{
    public override string IdempotencyKey => $"playback-progress:{SessionId}:{Seq}";
}

/// <summary>The session ended (watched to threshold or stopped). Consumed by Notifications. Keyed by the session.</summary>
public sealed record PlaybackCompleted(Guid SessionId, Guid UserId, Guid AssetId) : DomainEvent
{
    public override string IdempotencyKey => $"playback-completed:{SessionId}";
}

/// <summary>An FFmpeg transcode was started for a session. Keyed by the transcode job.</summary>
public sealed record TranscodeStarted(
    Guid TranscodeJobId,
    Guid SessionId,
    IReadOnlyList<string> Reasons) : DomainEvent
{
    public override string IdempotencyKey => $"transcode-started:{TranscodeJobId}";
}

/// <summary>An FFmpeg transcode failed. Consumed by Notifications. Keyed by the transcode job.</summary>
public sealed record TranscodeFailed(Guid TranscodeJobId, string Reason) : DomainEvent
{
    public override string IdempotencyKey => $"transcode-failed:{TranscodeJobId}";
}
