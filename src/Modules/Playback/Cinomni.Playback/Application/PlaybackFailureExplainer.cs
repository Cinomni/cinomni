using System.Globalization;
using Cinomni.Playback.Contracts;
using Cinomni.Playback.Persistence;
using Cinomni.Playback.Planning;

namespace Cinomni.Playback.Application;

/// <summary>
/// Puts a failed session into words for the person who was watching it: what went wrong, and what an
/// administrator can change so the next attempt plays. Only from what the session already records — its
/// end reason, its plan and whether the encoder fell back — never from FFmpeg's own output, which can
/// quote the media path and belongs on the transcode job.
/// <para>
/// A failure with nothing useful to add (an FFmpeg error part way through) gets no explanation: the
/// player's own message is as good as anything this could say without the details.
/// </para>
/// </summary>
internal static class PlaybackFailureExplainer
{
    public const string TooSlowCode = "playback.too_slow";
    public const string ResourceKilledCode = "playback.out_of_memory";
    public const string ServerRestartedCode = "playback.server_restarted";

    public static PlaybackFailureView? Explain(PlaybackSession session) => session.EndReason switch
    {
        PlaybackEndReason.TooSlow => new PlaybackFailureView(TooSlowCode, TooSlow(session)),
        PlaybackEndReason.ResourceKilled => new PlaybackFailureView(
            ResourceKilledCode,
            "The server stopped converting this video because it ran out of memory. An administrator can give "
            + "Cinomni more memory (CINOMNI_MEMORY) or lower Maximum resolution in Console → Settings → Playback."),
        PlaybackEndReason.ServerRestarted => new PlaybackFailureView(
            ServerRestartedCode,
            "The server restarted while this video was being converted. Play it again to continue where you were."),
        _ => null,
    };

    private static string TooSlow(PlaybackSession session)
    {
        var plan = session.Plan;
        var onCpu = plan.Backend == EncoderBackend.Software || session.TranscodeJobs.Any(job => job.FellBackToSoftware);
        var source = SourceDescription(plan);

        var what = onCpu
            ? $"The server could not convert this {source} on its CPU as fast as it plays."
            : $"The server could not convert this {source} as fast as it plays.";

        // Already brought down by the software ceiling, the encoder was not what fell behind: decoding the
        // full-size source was. A smaller picture would barely help, so it is not offered as the fix —
        // measured on a 4K Dolby Vision file that still ran at 0.80x with its output held to 1080 lines.
        var decodeBound = onCpu && plan.Decisions.Any(d => d is { Property: "softwareMaxHeight", Verdict: "fail" });

        var change = decodeBound
            ? "Reading a source this large takes more than the CPU it has. An administrator can set up a hardware "
                + "encoder that decodes it, or give Cinomni more CPU (CINOMNI_CPUS)."
            : onCpu
                ? "An administrator can lower Software encoder maximum resolution or Maximum resolution in Console → "
                    + "Settings → Playback, set up a hardware encoder, or give Cinomni more CPU (CINOMNI_CPUS)."
                : "An administrator can lower Maximum resolution in Console → Settings → Playback, or choose a faster "
                    + "Encoder speed.";

        return $"{what} {change}";
    }

    /// <summary>"4K HDR video", "1080p video" — as much as the plan recorded, and plainly "video" otherwise.</summary>
    private static string SourceDescription(PlaybackPlan plan)
    {
        var height = plan.Decisions
            .Where(d => d.Property == "videoHeight")
            .Select(d => int.TryParse(d.Actual, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : (int?)null)
            .FirstOrDefault();
        var hdr = plan.Decisions.Any(d => d.Property == "dynamicRange");

        var size = height switch
        {
            >= 2000 => "4K ",
            >= 1400 => "1440p ",
            >= 1000 => "1080p ",
            _ => string.Empty,
        };

        return $"{size}{(hdr ? "HDR " : string.Empty)}video";
    }
}
