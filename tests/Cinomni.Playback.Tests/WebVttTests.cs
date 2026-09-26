using Cinomni.Playback.Application;

namespace Cinomni.Playback.Tests;

/// <summary>Pure tests for the subtitle text a browser is handed: decoding a sidecar and SubRip to WebVTT.</summary>
public sealed class WebVttTests
{
    [Fact]
    public void SubRip_becomes_WebVTT_with_dotted_timestamps_and_no_ASS_overrides()
    {
        var srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\n{\\an8}Top line, 1,000 times\r\n\r\n";

        Assert.Equal(
            "WEBVTT\n\n1\n00:00:01.000 --> 00:00:02.500\nTop line, 1,000 times\n\n\n",
            WebVtt.FromSrt(srt));
    }

    [Fact]
    public void Text_that_is_not_valid_UTF8_is_read_as_Windows_1252()
    {
        byte[] latin = [(byte)'A', (byte)'d', (byte)'i', 0xF3, (byte)'s', (byte)' ', 0x80];

        Assert.Equal("Adiós €", WebVtt.Decode(latin));
    }

    [Fact]
    public void A_byte_order_mark_decides_the_encoding()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("Hola")).ToArray();
        var utf8 = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("¿Qué?")).ToArray();

        Assert.Equal("Hola", WebVtt.Decode(utf16));
        Assert.Equal("¿Qué?", WebVtt.Decode(utf8));
    }

    [Fact]
    public void A_file_that_is_not_WebVTT_is_not_passed_off_as_one()
    {
        Assert.Null(WebVtt.FromVtt("<script>alert(1)</script>"));
        Assert.Equal("WEBVTT\n\nx", WebVtt.FromVtt("﻿WEBVTT\r\n\r\nx"));
    }
}
