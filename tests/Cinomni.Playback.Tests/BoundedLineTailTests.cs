using Cinomni.Playback.Encoding;

namespace Cinomni.Playback.Tests;

/// <summary>
/// FFmpeg's stderr is drained for as long as the process lives, and the file it is fed is hostile, so
/// what is kept of it has to stay small whatever arrives — and still end with the reason it failed.
/// </summary>
public sealed class BoundedLineTailTests
{
    [Fact]
    public void Only_the_last_lines_are_kept_oldest_first()
    {
        var tail = new BoundedLineTail();

        for (var i = 1; i <= BoundedLineTail.MaxLines + 5; i++)
        {
            tail.Append($"line {i}\n");
        }

        var lines = tail.ToString().Split('\n');
        Assert.Equal(BoundedLineTail.MaxLines, lines.Length);
        Assert.Equal("line 6", lines[0]);
        Assert.Equal($"line {BoundedLineTail.MaxLines + 5}", lines[^1]);
    }

    [Fact]
    public void A_stream_without_line_breaks_cannot_grow_the_buffer()
    {
        var tail = new BoundedLineTail();

        for (var i = 0; i < 1_000; i++)
        {
            tail.Append(new string('x', 1_000));
        }

        Assert.Equal(BoundedLineTail.MaxLineLength, tail.ToString().Length);
    }

    [Fact]
    public void A_carriage_return_ends_a_line_and_blank_lines_take_no_slot()
    {
        var tail = new BoundedLineTail();

        tail.Append("frame=1\rframe=2\r\n\n\r\nInvalid data found when processing input\n");

        Assert.Equal("frame=1\nframe=2\nInvalid data found when processing input", tail.ToString());
    }

    [Fact]
    public void A_line_split_across_reads_is_one_line_and_an_unfinished_one_is_still_reported()
    {
        var tail = new BoundedLineTail();

        tail.Append("Conversion ");
        tail.Append("failed!\nError while ");
        tail.Append("decoding");

        Assert.Equal("Conversion failed!\nError while decoding", tail.ToString());
    }
}
