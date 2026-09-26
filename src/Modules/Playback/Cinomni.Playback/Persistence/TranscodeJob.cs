using Cinomni.Kernel.Identifiers;
using Cinomni.Playback.Contracts;

namespace Cinomni.Playback.Persistence;

/// <summary>
/// An FFmpeg transcode/remux job for a session (isolated process — a failure never takes down the
/// server). Holds the HLS output location the segment endpoints serve from.
/// <para>
/// Preparing → Running once FFmpeg has produced a manifest (or Failed if it never does). A running
/// job ends one of three ways: FFmpeg finishes the whole file (Completed), exits with an error part
/// way through (Failed, with the end of what it wrote to stderr), or its output is reclaimed when the
/// session ends or goes silent (Cleaned). A failed job stays Failed after its output is reclaimed:
/// the reason is worth more than the fact that the segments are gone.
/// </para>
/// </summary>
public sealed class TranscodeJob
{
    private const int PathMaxLength = 2048;
    private const int CodecMaxLength = 40;
    private const int ReasonMaxLength = 500;

    public Guid Id { get; init; }

    public Guid SessionId { get; init; }

    public TranscodeState State { get; private set; }

    public required string TargetVideoCodec { get; init; }

    public required string TargetAudioCodec { get; init; }

    public bool Hls { get; init; }

    /// <summary>Directory the HLS manifest and segments are written to (served, path-confined).</summary>
    public string? OutputPath { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The planned hardware backend exited before producing a manifest, and the encoder retried once with software, which is what actually ran.</summary>
    public bool FellBackToSoftware { get; private set; }

    public string? FallbackReason { get; private set; }

    /// <summary>
    /// The FFmpeg process producing the output, as the operating system knows it: its id and when it
    /// started. A process outlives its parent when the host crashes outside a container, and this pair
    /// is what lets the next run find and stop that one process — never another that reused the id.
    /// </summary>
    public int? ProcessId { get; private set; }

    public DateTimeOffset? ProcessStartedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public static TranscodeJob Prepare(
        Guid sessionId,
        string targetVideoCodec,
        string targetAudioCodec,
        bool hls,
        DateTimeOffset now) => new()
    {
        Id = Uuid7.New(),
        SessionId = sessionId,
        State = TranscodeState.Preparing,
        TargetVideoCodec = Text.Truncate(targetVideoCodec, CodecMaxLength)!,
        TargetAudioCodec = Text.Truncate(targetAudioCodec, CodecMaxLength)!,
        Hls = hls,
        CreatedAt = now,
    };

    /// <summary>Preparing → Running once FFmpeg has been spawned and its output location is known.</summary>
    public void MarkRunning(string outputPath, int? processId = null, DateTimeOffset? processStartedAt = null)
    {
        OutputPath = Text.Truncate(outputPath, PathMaxLength);
        ProcessId = processId;
        ProcessStartedAt = processStartedAt;
        State = TranscodeState.Running;
    }

    /// <summary>Running/Preparing → Failed (exit≠0, timeout, or the binary is missing).</summary>
    public void MarkFailed(string reason)
    {
        LastError = Text.Truncate(reason, ReasonMaxLength);
        State = TranscodeState.Failed;
    }

    /// <summary>
    /// Records how FFmpeg ended on its own: code 0 means it wrote the whole file (Completed), anything
    /// else is a mid-stream failure, explained by the end of its stderr. Only a running job changes —
    /// an outcome observed after the output was reclaimed, or twice, is not news.
    /// </summary>
    /// <returns><c>true</c> when the job changed state.</returns>
    public bool RecordExit(int exitCode, string diagnostics)
    {
        if (State is not (TranscodeState.Running or TranscodeState.Segmenting))
        {
            return false;
        }

        if (exitCode == 0)
        {
            State = TranscodeState.Completed;
            return true;
        }

        MarkFailed(Explain($"FFmpeg exited with code {exitCode}.", diagnostics));
        return true;
    }

    /// <summary>
    /// The reason a failed job records: a summary that names no path, then as much of the end of
    /// FFmpeg's stderr as the column holds — the end, because that is where FFmpeg says what went wrong.
    /// </summary>
    public static string Explain(string summary, string diagnostics) =>
        diagnostics.Length == 0
            ? summary
            : $"{summary} {Text.Tail(diagnostics, Math.Max(0, ReasonMaxLength - summary.Length - 1))}";

    /// <summary>
    /// The output directory has been removed. A failed job keeps its state and its reason; everything
    /// else ends here.
    /// </summary>
    /// <returns><c>true</c> when the job changed state.</returns>
    public bool MarkCleaned()
    {
        if (State is TranscodeState.Cleaned or TranscodeState.Failed)
        {
            return false;
        }

        State = TranscodeState.Cleaned;
        return true;
    }

    /// <summary>Records that the encoder fell back to software after the planned hardware backend failed. Does not change <see cref="State"/> — the job still runs, just not on the backend the plan named.</summary>
    public void RecordFallback(string reason)
    {
        FellBackToSoftware = true;
        FallbackReason = Text.Truncate(reason, ReasonMaxLength);
    }
}
