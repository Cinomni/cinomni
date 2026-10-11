using System.Globalization;
using System.Text;
using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Generated dialogue for the synchronization tests: a subtitle's cues, and the speech a detector would
/// find in the audio when those lines are really spoken at <c>t × ratio + offset</c>. Seeded, so every
/// run is the same film. The speech is made imperfect on purpose — it starts and ends a little off the
/// cue and some frames are flipped — because a search that only works on a perfect copy proves nothing.
/// </summary>
internal sealed class SyntheticDialogue
{
    private readonly Random _random;

    public SyntheticDialogue(int seed, TimeSpan duration)
    {
        _random = new Random(seed);
        Cues = Generate(duration);
    }

    public IReadOnlyList<SubtitleCue> Cues { get; }

    /// <summary>The speech in one stretch of audio when the cues are really spoken at <c>t × ratio + offset</c>.</summary>
    public SpeechWindow Speech(TimeSpan start, TimeSpan length, long offsetMs, double ratio = 1.0, double noise = 0.03)
    {
        var frames = (int)(length.TotalMilliseconds / SpeechDetector.FrameMs);
        var speech = new bool[frames];
        var startMs = (long)start.TotalMilliseconds;
        foreach (var cue in Cues)
        {
            // Speech starts and stops a little either side of the cue: a subtitler's timing is not a
            // sample-accurate transcript, and neither is a detector's.
            var from = (long)(cue.StartMs * ratio) + offsetMs + _random.Next(-120, 121);
            var to = (long)(cue.EndMs * ratio) + offsetMs + _random.Next(-120, 121);
            Mark(speech, startMs, from, to);
        }

        for (var i = 0; i < speech.Length; i++)
        {
            if (_random.NextDouble() < noise)
            {
                speech[i] = !speech[i];
            }
        }

        return new SpeechWindow(startMs, speech);
    }

    /// <summary>Speech that has nothing to do with the cues: the same amount of talk, at random.</summary>
    public SpeechWindow RandomSpeech(TimeSpan start, TimeSpan length)
    {
        var frames = (int)(length.TotalMilliseconds / SpeechDetector.FrameMs);
        var speech = new bool[frames];
        var at = 0;
        while (at < frames)
        {
            var talk = _random.Next(100, 400);
            var pause = _random.Next(50, 600);
            Array.Fill(speech, true, at, Math.Min(talk, frames - at));
            at += talk + pause;
        }

        return new SpeechWindow((long)start.TotalMilliseconds, speech);
    }

    /// <summary>The cues as a SubRip file, numbered, with one line of text each.</summary>
    public string ToSrt(string lineEnding = "\n")
    {
        var builder = new StringBuilder();
        for (var i = 0; i < Cues.Count; i++)
        {
            builder.Append(i + 1).Append(lineEnding)
                .Append(Timestamp(Cues[i].StartMs)).Append(" --> ").Append(Timestamp(Cues[i].EndMs)).Append(lineEnding)
                .Append("Line ").Append(i + 1).Append(lineEnding)
                .Append(lineEnding);
        }

        return builder.ToString();
    }

    public static string Timestamp(long ms)
    {
        var value = TimeSpan.FromMilliseconds(ms);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");
    }

    private List<SubtitleCue> Generate(TimeSpan duration)
    {
        var cues = new List<SubtitleCue>();
        var at = 2_000L + _random.Next(0, 3_000);
        var end = (long)duration.TotalMilliseconds;
        while (at < end)
        {
            var length = _random.Next(900, 4_500);
            cues.Add(new SubtitleCue(at, at + length));

            // Mostly short gaps, now and then a long silence: dialogue, not a metronome.
            at += length + (_random.NextDouble() < 0.15 ? _random.Next(5_000, 25_000) : _random.Next(300, 4_000));
        }

        return cues;
    }

    private static void Mark(bool[] speech, long windowStartMs, long fromMs, long toMs)
    {
        var from = (fromMs - windowStartMs) / SpeechDetector.FrameMs;
        var to = (toMs - windowStartMs) / SpeechDetector.FrameMs;
        for (var i = Math.Max(0, from); i < Math.Min(speech.Length, to); i++)
        {
            speech[i] = true;
        }
    }
}
