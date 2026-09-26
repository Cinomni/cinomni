using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Cinomni.Kernel.Diagnostics;
using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Diagnostics;

/// <summary>
/// How much of this node's CPU playback is actually asking for. A transcode is by far the most
/// expensive thing a single-node installation does, and one concurrent transcode too many is the
/// difference between a household watching and a household buffering.
/// <para>
/// <b>No account id, no session id, no asset id, no path.</b> Who is watching what is the single most
/// private fact in the product, and none of it is needed to answer the operator's question, which is
/// "how many transcodes are running and how often do they fail to start".
/// </para>
/// </summary>
public static class PlaybackMetrics
{
    private static readonly Meter Meter = new(CinomniTelemetry.MeterName);

    private static readonly KeyValuePair<string, object?> ModuleTag =
        new(CinomniTelemetry.Tags.Module, CinomniTelemetry.Modules.Playback);

    /// <summary>
    /// The sessions this process is counting, keyed by session id so a second start or a redelivered
    /// stop cannot move the number twice. <b>The key never leaves this dictionary</b> — it is a lookup
    /// handle, not a label, and only the count is ever published.
    /// <para>
    /// Keeping the identity rather than a bare integer is what makes every way a session can end safe to
    /// report: an id this process never counted — one that belongs to a session started before the last
    /// restart — is simply absent, so uncounting it does nothing instead of subtracting somebody else's
    /// running transcode.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, byte> Encoding = new();

    private static readonly Counter<long> Started = Meter.CreateCounter<long>(
        "cinomni.transcode.started",
        unit: "{session}",
        description: "Transcode sessions started, by playback method (Transcode or Remux).");

    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        "cinomni.transcode.failed",
        unit: "{session}",
        description: "Transcode sessions that could not be started, by playback method.");

    private static readonly Counter<long> Refused = Meter.CreateCounter<long>(
        "cinomni.transcode.refused",
        unit: "{session}",
        description: "Transcode sessions refused because the node or the account was at its limit, by playback method.");

    static PlaybackMetrics() =>
        Meter.CreateObservableGauge(
            "cinomni.transcode.active",
            () => new Measurement<long>(Encoding.Count, ModuleTag),
            unit: "{session}",
            description: "Streams this node is converting, or serving from a conversion, right now.");

    /// <summary>Records a transcode that started.</summary>
    /// <param name="sessionId">Lookup key only. Never published, never tagged, never exported.</param>
    /// <param name="method">The bounded method name — <c>Transcode</c> or <c>Remux</c>, never a path.</param>
    public static void RecordStarted(Guid sessionId, string method)
    {
        Encoding.TryAdd(sessionId, 0);
        Started.Add(1, ModuleTag, new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Method, method));
    }

    /// <summary>
    /// Counts a stream a previous run left finished on disk and this run took back. It was started —
    /// and counted as started — by that run, so only the gauge moves.
    /// </summary>
    /// <param name="sessionId">Lookup key only. Never published, never tagged, never exported.</param>
    public static void RecordAdopted(Guid sessionId) => Encoding.TryAdd(sessionId, 0);

    /// <summary>
    /// Records a transcode that failed to start. The FFmpeg failure text is not passed in: it quotes
    /// the media path and the argument list.
    /// </summary>
    public static void RecordFailed(string method) =>
        Failed.Add(1, ModuleTag, new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Method, method));

    /// <summary>
    /// Records a transcode that was never started because a limit was reached. A steady trickle of
    /// these is the operator's cue that the limit is lower than the household's evenings need.
    /// </summary>
    public static void RecordRefused(string method) =>
        Refused.Add(1, ModuleTag, new KeyValuePair<string, object?>(CinomniTelemetry.Tags.Method, method));

    /// <summary>
    /// Records a transcode leaving the node. Safe to call for any session, at any time, as often as you
    /// like: only an id this process is currently counting changes the number.
    /// <para>
    /// That is what lets every way a stream can end report it. The registry of running transcodes calls
    /// it whenever a stream leaves — a stop, an idle or expired stream, revoked access, a failure, a
    /// shutdown — and the retention purge calls it again as a backstop. The watched threshold does not:
    /// the session completes there while the credits are still being converted. A direct-play session
    /// was never counted — it costs no FFmpeg process — so uncounting one does nothing rather than
    /// subtracting somebody else's transcode.
    /// </para>
    /// </summary>
    /// <param name="sessionId">Lookup key only. Never published, never tagged, never exported.</param>
    public static void RecordStopped(Guid sessionId) => Encoding.TryRemove(sessionId, out _);

    /// <summary>
    /// Whether a playback method puts an FFmpeg process on this node. Remux copies streams and
    /// transcode re-encodes; both are processes this gauge counts. Direct play is a file being read.
    /// </summary>
    public static bool IsEncoded(PlaybackMethod method) => method is not PlaybackMethod.DirectPlay;
}
