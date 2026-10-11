using Cinomni.Library.Contracts;

namespace Cinomni.Subtitles.Sync;

/// <summary>
/// Which stretches of the video are decoded: one near the start, one in the middle and one near the
/// end, so a subtitle that drifts (a frame-rate mismatch) shows up as three different offsets instead
/// of passing as one. The start and end stretches stay clear of the opening and closing credits, which
/// carry music and no dialogue. A video too short for three stretches, or whose length nobody
/// recorded, is sampled once from the beginning.
/// </summary>
public static class SyncWindowPlan
{
    private static readonly TimeSpan ShortestForThree = TimeSpan.FromMinutes(12);

    public static IReadOnlyList<(TimeSpan Start, TimeSpan Length)> For(double? durationSeconds, TimeSpan maxLength)
    {
        if (durationSeconds is not { } seconds || !double.IsFinite(seconds) || seconds <= 0)
        {
            return [(TimeSpan.Zero, maxLength)];
        }

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration < ShortestForThree)
        {
            return [(TimeSpan.Zero, duration < maxLength ? duration : maxLength)];
        }

        var length = duration / 4 < maxLength ? duration / 4 : maxLength;
        return
        [
            (duration * 0.10, length),
            ((duration / 2) - (length / 2), length),
            (duration * 0.88 - length, length),
        ];
    }
}

/// <summary>
/// Which audio track speech is read from: the one in the subtitle's language, else the default one,
/// else the first. A subtitle is a transcript of one language's dialogue, and a dub is timed to the
/// same picture but not always to the same breaths.
/// </summary>
public static class SyncAudioTrack
{
    // ISO 639-1 codes (what the subtitle preference holds) to the 639-2 codes containers usually tag
    // audio with, bibliographic and terminological forms both. Languages outside this list match only
    // when the track is tagged with the same two letters.
    private static readonly Dictionary<string, string[]> ThreeLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["eng"], ["es"] = ["spa"], ["fr"] = ["fra", "fre"], ["de"] = ["deu", "ger"],
        ["it"] = ["ita"], ["pt"] = ["por"], ["nl"] = ["nld", "dut"], ["ru"] = ["rus"],
        ["ja"] = ["jpn"], ["zh"] = ["zho", "chi"], ["ko"] = ["kor"], ["ar"] = ["ara"],
        ["sv"] = ["swe"], ["no"] = ["nor", "nob", "nno"], ["da"] = ["dan"], ["fi"] = ["fin"],
        ["pl"] = ["pol"], ["tr"] = ["tur"], ["cs"] = ["ces", "cze"], ["el"] = ["ell", "gre"],
        ["he"] = ["heb"], ["hu"] = ["hun"], ["ro"] = ["ron", "rum"], ["ca"] = ["cat"],
        ["uk"] = ["ukr"], ["hi"] = ["hin"], ["th"] = ["tha"], ["vi"] = ["vie"],
        ["id"] = ["ind"], ["bg"] = ["bul"], ["hr"] = ["hrv"], ["sr"] = ["srp"],
        ["sk"] = ["slk", "slo"], ["sl"] = ["slv"], ["eu"] = ["eus", "baq"], ["gl"] = ["glg"],
    };

    /// <summary>The stream index to decode, or null when the video has no audio at all.</summary>
    public static int? Choose(IReadOnlyList<MediaStreamSummary> streams, string subtitleLanguage)
    {
        var audio = streams.Where(stream => stream.Type == MediaStreamType.Audio && !stream.IsExternal).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        var inLanguage = audio.Where(stream => Matches(subtitleLanguage, stream.Language)).ToList();
        var chosen = inLanguage.FirstOrDefault(stream => stream.IsDefault)
            ?? inLanguage.FirstOrDefault()
            ?? audio.FirstOrDefault(stream => stream.IsDefault)
            ?? audio[0];
        return chosen.StreamIndex;
    }

    public static bool Matches(string subtitleLanguage, string? trackLanguage)
    {
        if (string.IsNullOrWhiteSpace(trackLanguage))
        {
            return false;
        }

        var track = trackLanguage.Trim();
        return string.Equals(subtitleLanguage, track, StringComparison.OrdinalIgnoreCase)
            || (ThreeLetter.TryGetValue(subtitleLanguage, out var codes)
                && codes.Contains(track, StringComparer.OrdinalIgnoreCase));
    }
}
