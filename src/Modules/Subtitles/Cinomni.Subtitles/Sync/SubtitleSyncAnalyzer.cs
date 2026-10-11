using System.Text;
using Cinomni.Library.Contracts;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Files;

namespace Cinomni.Subtitles.Sync;

/// <summary>A pass's verdict, and the rewritten file when the verdict is to apply it.</summary>
public sealed record SyncAnalysis(SyncVerdict Verdict, byte[]? Shifted);

/// <summary>What a pass needs to know about the subtitle it analyzes.</summary>
public sealed record SyncSubject(
    Guid MediaAssetId,
    string Language,
    bool Forced,
    SubtitleFormat Format,
    string OriginalPath);

/// <summary>
/// One synchronization pass over one subtitle, start to finish, with no database in it: it reads the
/// original download, decides which audio to listen to and where, runs the speech source over those
/// stretches, searches the best placement and decides. Everything slow or external happens here, which
/// is why it runs outside any transaction; <see cref="SubtitleSyncProcessor"/> persists the verdict in a
/// short one afterwards.
/// <para>
/// Every way it can fall short returns a verdict rather than throwing: a subtitle that cannot be synced
/// is <see cref="SubtitleSyncOutcome.Unsupported"/>, one whose analysis could not run is
/// <see cref="SubtitleSyncOutcome.Failed"/>, and both keep the original. A failure's reason is only the
/// cause ("FFmpeg could not be started"): the processor completes the sentence, because only it knows
/// whether what is kept is the download or an earlier synchronized copy.
/// </para>
/// </summary>
public sealed class SubtitleSyncAnalyzer(
    ILibraryQuery library,
    ISubtitleFileStore fileStore,
    ISpeechActivitySource speech,
    SubtitleSyncOptions options)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<SyncAnalysis> AnalyzeAsync(SyncSubject subject, CancellationToken cancellationToken = default)
    {
        // Turned off since this pass was queued (a re-sync, or a backlog from before the switch). That
        // says nothing about the subtitle, so it is a failure that keeps what is played — reported as
        // unsupported, it would put the original back over a copy an earlier pass had applied.
        if (!options.Enabled)
        {
            return Keep(SubtitleSyncOutcome.Failed, "synchronization is turned off");
        }

        if (Unsupported(subject.Format, subject.Forced, options) is { } reason)
        {
            return Keep(SubtitleSyncOutcome.Unsupported, reason);
        }

        var asset = await library.GetAsync(new MediaAssetId(subject.MediaAssetId), cancellationToken);
        var version = asset?.Versions.FirstOrDefault(v => v.Id == asset.Asset.PrimaryVersionId) ?? asset?.Versions.FirstOrDefault();
        if (version is null)
        {
            return Keep(SubtitleSyncOutcome.Unsupported, "Not synchronized: the video is no longer in the library.");
        }

        if (SyncAudioTrack.Choose(version.Streams, subject.Language) is not { } audioStream)
        {
            return Keep(SubtitleSyncOutcome.Unsupported, "Not synchronized: the video has no audio track to compare with.");
        }

        var bytes = await fileStore.ReadAsync(subject.OriginalPath, cancellationToken);
        if (bytes is null)
        {
            return Keep(SubtitleSyncOutcome.Failed, "the downloaded file could not be read");
        }

        if (!SubtitleTimingDocument.TryParse(SubtitleTextDecoder.Decode(bytes), subject.Format, out var document, out var failure))
        {
            return Keep(SubtitleSyncOutcome.Unsupported, $"Not synchronized: {failure}.");
        }

        var plan = SyncWindowPlan.For(version.DurationSeconds, options.WindowLength);
        var windows = new List<SpeechWindow>(plan.Count);
        foreach (var (start, length) in plan)
        {
            var activity = await speech.DetectAsync(version.FullPath, audioStream, start, length, cancellationToken);
            if (activity.Failure is not null || activity.Speech is null)
            {
                return Keep(SubtitleSyncOutcome.Failed, activity.Failure ?? "the audio could not be read");
            }

            windows.Add(new SpeechWindow((long)start.TotalMilliseconds, activity.Speech));
        }

        var verdict = SyncDecider.Decide(OffsetSearch.Estimate(windows, document!.Cues), plan.Count, options);
        return verdict.Outcome == SubtitleSyncOutcome.Applied
            ? new SyncAnalysis(verdict, Encode(document.Shift(verdict.OffsetMs, verdict.Ratio), bytes))
            : new SyncAnalysis(verdict, null);
    }

    /// <summary>
    /// The copy is always UTF-8, whatever the download was decoded from — every player reads it, and a
    /// legacy code page cannot hold every character a decoded file may contain. A download that started
    /// with a UTF-8 byte order mark gets one back, because some players only take UTF-8 when it is marked.
    /// </summary>
    private static byte[] Encode(string text, byte[] original)
    {
        var encoded = Utf8.GetBytes(text);
        return original.AsSpan().StartsWith(Utf8Bom) ? [.. Utf8Bom, .. encoded] : encoded;
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Why a subtitle is out of scope before anything is decoded, or null when it is in scope. ASS
    /// styling carries positioned, layered and animated events whose timing a line rewrite would not
    /// keep coherent, and a forced track has too few lines to match speech with any confidence.
    /// </summary>
    public static string? Unsupported(SubtitleFormat format, bool forced, SubtitleSyncOptions options) =>
        !options.Enabled ? "Not synchronized: synchronization is turned off."
        : forced ? "Not synchronized: forced subtitles are not synchronized."
        : format is not (SubtitleFormat.Srt or SubtitleFormat.Vtt) ? "Not synchronized: only SubRip and WebVTT files are synchronized."
        : null;

    private static SyncAnalysis Keep(SubtitleSyncOutcome outcome, string reason) =>
        new(SyncVerdict.Unmeasured(outcome, reason), null);
}
