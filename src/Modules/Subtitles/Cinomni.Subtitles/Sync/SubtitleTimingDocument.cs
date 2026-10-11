using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cinomni.Subtitles.Contracts;

namespace Cinomni.Subtitles.Sync;

/// <summary>One timed cue, in milliseconds from the start of the file.</summary>
public readonly record struct SubtitleCue(long StartMs, long EndMs);

/// <summary>
/// A SubRip or WebVTT file read for its timing, and rewritten with a new one.
/// <para>
/// The file is a provider's download and therefore hostile. It is read line by line and never
/// evaluated: a line is a timing line only if it matches a strict pattern, a timestamp is accepted only
/// with minutes and seconds under 60 and the whole under <see cref="MaxTimestamp"/>, and past
/// <see cref="MaxCues"/> cues the file is refused outright rather than half-read. A cue that ends before
/// it starts is kept in the file but left out of the analysis, because it says nothing about when
/// anybody speaks.
/// </para>
/// <para>
/// Rewriting touches the timing lines and nothing else: every other line, and each line's own ending,
/// is copied as it was. A cue shifted before zero is clamped to start at zero; one that ends before zero
/// collapses to an empty cue at zero, which no player shows, rather than being cut out of a file whose
/// numbering and blocks are not ours to renumber.
/// </para>
/// </summary>
public sealed partial class SubtitleTimingDocument
{
    public const int MaxCues = 20_000;

    public static readonly TimeSpan MaxTimestamp = TimeSpan.FromHours(48);

    private readonly List<Line> _lines;
    private readonly SubtitleFormat _format;

    private SubtitleTimingDocument(List<Line> lines, SubtitleFormat format, IReadOnlyList<SubtitleCue> cues)
    {
        _lines = lines;
        _format = format;
        Cues = cues;
    }

    /// <summary>The well-formed cues, in file order: what the analysis compares with speech.</summary>
    public IReadOnlyList<SubtitleCue> Cues { get; }

    // Hours are optional because WebVTT allows mm:ss.ttt; the fraction is one to three digits because
    // real SubRip files are not always written with three.
    [GeneratedRegex(
        @"^(?<lead>[ \t]*)(?<start>(?:\d{1,3}:)?\d{1,2}:\d{1,2}[,.]\d{1,3})(?<arrow>[ \t]*-->[ \t]*)(?<end>(?:\d{1,3}:)?\d{1,2}:\d{1,2}[,.]\d{1,3})(?<rest>.*)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex TimingLine();

    /// <summary>The file's timing, or a reason it cannot be synchronized.</summary>
    public static bool TryParse(string text, SubtitleFormat format, out SubtitleTimingDocument? document, out string? failure)
    {
        document = null;
        failure = null;
        if (format is not (SubtitleFormat.Srt or SubtitleFormat.Vtt))
        {
            failure = "only SubRip and WebVTT timing can be rewritten";
            return false;
        }

        var lines = Split(text.TrimStart('﻿'));
        var cues = new List<SubtitleCue>();
        var timed = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryReadTiming(lines[i].Text, out var start, out var end, out var match))
            {
                continue;
            }

            if (++timed > MaxCues)
            {
                failure = $"the file has more than {MaxCues} cues";
                return false;
            }

            lines[i] = lines[i] with { Timing = match };
            if (end > start)
            {
                cues.Add(new SubtitleCue(start, end));
            }
        }

        if (cues.Count == 0)
        {
            failure = "the file has no usable cues";
            return false;
        }

        document = new SubtitleTimingDocument(lines, format, cues);
        return true;
    }

    /// <summary>
    /// The file with every cue moved to <c>t × ratio + offset</c>. Deterministic: the same original,
    /// offset and ratio always produce the same text, so a pass interrupted after writing it and run
    /// again writes the same bytes.
    /// </summary>
    public string Shift(long offsetMs, double ratio)
    {
        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            if (line.Timing is not { } match
                || !TryParseTimestamp(match.Groups["start"].Value, out var start)
                || !TryParseTimestamp(match.Groups["end"].Value, out var end))
            {
                builder.Append(line.Text).Append(line.Ending);
                continue;
            }

            var shiftedEnd = Math.Max(0, Transform(end, offsetMs, ratio));
            var shiftedStart = Math.Max(0, Transform(start, offsetMs, ratio));
            builder
                .Append(match.Groups["lead"].Value)
                .Append(Format(shiftedStart))
                .Append(match.Groups["arrow"].Value)
                .Append(Format(shiftedEnd))
                .Append(match.Groups["rest"].Value)
                .Append(line.Ending);
        }

        return builder.ToString();
    }

    private static long Transform(long ms, long offsetMs, double ratio) =>
        (long)Math.Round((ms * ratio) + offsetMs, MidpointRounding.AwayFromZero);

    private string Format(long ms)
    {
        // Clamped below 48 h on the way out as well: a forward shift of the last cue must not write a
        // timestamp this parser would refuse on the next pass.
        var value = TimeSpan.FromMilliseconds(Math.Min(ms, (long)MaxTimestamp.TotalMilliseconds - 1));
        var separator = _format == SubtitleFormat.Srt ? ',' : '.';
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}{separator}{value.Milliseconds:000}");
    }

    private static bool TryReadTiming(string text, out long start, out long end, out Match match)
    {
        start = 0;
        end = 0;
        match = Match.Empty;
        if (!text.Contains("-->", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            match = TimingLine().Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return match.Success
            && TryParseTimestamp(match.Groups["start"].Value, out start)
            && TryParseTimestamp(match.Groups["end"].Value, out end);
    }

    private static bool TryParseTimestamp(string token, out long ms)
    {
        ms = 0;
        var fractionAt = token.IndexOfAny([',', '.']);
        var clock = token[..fractionAt].Split(':');
        var fraction = token[(fractionAt + 1)..];

        long hours = 0;
        var at = 0;
        if (clock.Length == 3)
        {
            hours = long.Parse(clock[0], CultureInfo.InvariantCulture);
            at = 1;
        }

        var minutes = long.Parse(clock[at], CultureInfo.InvariantCulture);
        var seconds = long.Parse(clock[at + 1], CultureInfo.InvariantCulture);
        if (minutes >= 60 || seconds >= 60)
        {
            return false;
        }

        // "5" after the separator is half a second, not five milliseconds.
        var millis = long.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
        ms = (((hours * 60) + minutes) * 60 + seconds) * 1000 + millis;
        return ms < (long)MaxTimestamp.TotalMilliseconds;
    }

    /// <summary>The lines with the exact ending each one had, so the rewrite can give it back.</summary>
    private static List<Line> Split(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r'))
            {
                continue;
            }

            var ending = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : text[i].ToString();
            lines.Add(new Line(text[start..i], ending, null));
            i += ending.Length - 1;
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new Line(text[start..], string.Empty, null));
        }

        return lines;
    }

    private sealed record Line(string Text, string Ending, Match? Timing);
}
