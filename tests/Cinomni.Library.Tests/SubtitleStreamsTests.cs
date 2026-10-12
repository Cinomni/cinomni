using Cinomni.Library.Contracts;

namespace Cinomni.Library.Tests;

/// <summary>Pure unit tests for telling a text subtitle stream from a picture one.</summary>
public sealed class SubtitleStreamsTests
{
    [Theory]
    [InlineData("subrip")]
    [InlineData("ass")]
    [InlineData("webvtt")]
    [InlineData("mov_text")]
    [InlineData("SubRip")]
    public void An_embedded_text_codec_is_text(string codec) =>
        Assert.True(SubtitleStreams.IsText(Subtitle(codec, external: false)));

    [Theory]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("dvd_subtitle")]
    [InlineData("dvb_subtitle")]
    [InlineData(null)]
    public void An_embedded_picture_or_unknown_codec_is_not_text(string? codec) =>
        Assert.False(SubtitleStreams.IsText(Subtitle(codec, external: false)));

    [Fact]
    public void A_sidecar_is_text_whatever_its_codec_reads() =>
        Assert.True(SubtitleStreams.IsText(Subtitle("external", external: true)));

    [Fact]
    public void A_stream_that_is_not_a_subtitle_is_not_text() =>
        Assert.False(SubtitleStreams.IsText(
            new MediaStreamSummary(1, MediaStreamType.Audio, "text", "eng", 2, null, null, null, null, true, false, false)));

    private static MediaStreamSummary Subtitle(string? codec, bool external) =>
        new(2, MediaStreamType.Subtitle, codec, "eng", null, null, null, null, null, false, false, external);
}
