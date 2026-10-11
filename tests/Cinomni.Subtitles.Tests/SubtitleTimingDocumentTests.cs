using System.Text;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Sync;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// Pure tests of reading and rewriting subtitle timing. A provider's file is hostile input: these pin
/// what is refused, what is skipped, and that a rewrite changes the timing lines and nothing else.
/// </summary>
public sealed class SubtitleTimingDocumentTests
{
    [Fact]
    public void A_shift_rewrites_only_the_timing_lines()
    {
        // Arrange — markup, an arrow inside dialogue and a trailing position are all someone else's text.
        const string srt = "1\n00:00:01,000 --> 00:00:02,500 X1:10 X2:20\n<i>He said --> go</i>\n\n2\n00:01:00,000 --> 00:01:03,000\nBye\n";

        // Act
        var shifted = Parse(srt).Shift(1_240, 1.0);

        // Assert
        Assert.Equal(
            "1\n00:00:02,240 --> 00:00:03,740 X1:10 X2:20\n<i>He said --> go</i>\n\n2\n00:01:01,240 --> 00:01:04,240\nBye\n",
            shifted);
    }

    [Fact]
    public void A_marked_crlf_download_is_decoded_and_rewritten_with_its_line_endings()
    {
        // Arrange — UTF-8 with a BOM and Windows line endings, as many downloads are. The text comes
        // back without the mark; the analyzer puts it back on the bytes it writes.
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("1\r\n00:00:05,000 --> 00:00:06,000\r\nÇa va ?\r\n"))
            .ToArray();

        // Act
        var shifted = Parse(SubtitleTextDecoder.Decode(bytes)).Shift(-1_000, 1.0);

        // Assert
        Assert.Equal("1\r\n00:00:04,000 --> 00:00:05,000\r\nÇa va ?\r\n", shifted);
    }

    [Fact]
    public void An_unmarked_file_that_is_not_utf8_is_read_as_windows_1252()
    {
        var text = SubtitleTextDecoder.Decode([0x43, 0x61, 0x66, 0xE9]); // "Café" in Windows-1252

        Assert.Equal("Café", text);
    }

    [Fact]
    public void Cues_shifted_before_zero_are_clamped_rather_than_written_negative()
    {
        const string srt = "1\n00:00:00,500 --> 00:00:01,200\nA\n\n2\n00:00:01,000 --> 00:00:03,000\nB\n";

        var shifted = Parse(srt).Shift(-1_500, 1.0);

        Assert.Equal("1\n00:00:00,000 --> 00:00:00,000\nA\n\n2\n00:00:00,000 --> 00:00:01,500\nB\n", shifted);
    }

    [Fact]
    public void Garbage_and_out_of_range_timestamps_are_neither_cues_nor_rewritten()
    {
        // Arrange — minutes past 59, a timestamp past 48 h, words, and an inverted cue.
        const string srt =
            "1\n00:99:00,000 --> 00:99:01,000\nA\n\n" +
            "2\n999:00:00,000 --> 999:00:01,000\nB\n\n" +
            "3\nsoon --> later\nC\n\n" +
            "4\n00:00:09,000 --> 00:00:08,000\nD\n\n" +
            "5\n00:00:10,000 --> 00:00:11,000\nE\n";

        // Act
        var document = Parse(srt);
        var shifted = document.Shift(1_000, 1.0);

        // Assert — one usable cue; the inverted one is still moved so it stays where it was relative to
        // its neighbours, and the unreadable lines are copied as they were.
        Assert.Equal([new SubtitleCue(10_000, 11_000)], document.Cues);
        Assert.Contains("00:99:00,000 --> 00:99:01,000", shifted, StringComparison.Ordinal);
        Assert.Contains("999:00:00,000 --> 999:00:01,000", shifted, StringComparison.Ordinal);
        Assert.Contains("soon --> later", shifted, StringComparison.Ordinal);
        Assert.Contains("00:00:10,000 --> 00:00:09,000", shifted, StringComparison.Ordinal);
        Assert.Contains("00:00:11,000 --> 00:00:12,000", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void More_cues_than_the_cap_are_refused_outright()
    {
        var builder = new StringBuilder();
        for (var i = 0; i <= SubtitleTimingDocument.MaxCues; i++)
        {
            builder.Append(i + 1).Append('\n')
                .Append(SyntheticDialogue.Timestamp(i * 1_000L)).Append(" --> ")
                .Append(SyntheticDialogue.Timestamp((i * 1_000L) + 500)).Append("\nx\n\n");
        }

        var parsed = SubtitleTimingDocument.TryParse(builder.ToString(), SubtitleFormat.Srt, out var document, out var failure);

        Assert.False(parsed);
        Assert.Null(document);
        Assert.Contains("more than 20000 cues", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_cues_and_an_ass_file_are_refused()
    {
        Assert.False(SubtitleTimingDocument.TryParse("just some text\n", SubtitleFormat.Srt, out _, out var empty));
        Assert.Equal("the file has no usable cues", empty);

        Assert.False(SubtitleTimingDocument.TryParse("1\n00:00:01,000 --> 00:00:02,000\nA\n", SubtitleFormat.Ass, out _, out _));
    }

    [Fact]
    public void WebVtt_short_timestamps_and_cue_settings_are_read_and_kept()
    {
        const string vtt = "WEBVTT\n\n00:01.000 --> 00:02.500 align:start\nHi\n";

        var document = Parse(vtt, SubtitleFormat.Vtt);
        var shifted = document.Shift(1_000, 1.0);

        Assert.Equal([new SubtitleCue(1_000, 2_500)], document.Cues);
        Assert.Equal("WEBVTT\n\n00:00:02.000 --> 00:00:03.500 align:start\nHi\n", shifted);
    }

    [Fact]
    public void A_ratio_scales_every_timestamp_before_the_offset_is_added()
    {
        const string srt = "1\n01:00:00,000 --> 01:00:02,000\nA\n";

        var shifted = Parse(srt).Shift(500, 24000 / (25 * 1001.0));

        // 3,600,000 ms × 0.959041 = 3,452,547 ms, plus 500.
        Assert.Contains("00:57:33,047 --> 00:57:34,966", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_or_two_digit_fraction_is_a_fraction_of_a_second()
    {
        var document = Parse("1\n00:00:01,5 --> 00:00:02,25\nA\n");

        Assert.Equal([new SubtitleCue(1_500, 2_250)], document.Cues);
    }

    private static SubtitleTimingDocument Parse(string text, SubtitleFormat format = SubtitleFormat.Srt)
    {
        Assert.True(SubtitleTimingDocument.TryParse(text, format, out var document, out var failure), failure);
        return document!;
    }

    [Fact]
    public void A_file_of_nothing_but_line_breaks_is_refused_before_it_is_split()
    {
        var text = new string('\n', SubtitleTimingDocument.MaxLines + 1);

        Assert.False(SubtitleTimingDocument.TryParse(text, SubtitleFormat.Srt, out _, out var failure));
        Assert.Contains("lines", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Digits_from_another_script_are_not_a_timestamp_and_do_not_throw()
    {
        // Arabic-Indic digits match \d in .NET; read as a timestamp they would fail to parse.
        const string srt = "1\n\u0660\u0660:\u0660\u0660:\u0660\u0661,\u0660\u0660\u0660 --> 00:00:02,000\nA\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n";

        Assert.True(SubtitleTimingDocument.TryParse(srt, SubtitleFormat.Srt, out var document, out _));
        Assert.Single(document!.Cues);
    }
}
