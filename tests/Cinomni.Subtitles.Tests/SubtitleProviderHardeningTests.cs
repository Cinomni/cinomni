using System.Net;
using System.Text;
using System.Text.Json;
using Cinomni.Subtitles.Contracts;
using Cinomni.Subtitles.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// What the providers hand on and what they believe: the OpenSubtitles key stays with the API it is
/// for, and a SubDL file's format comes from its declared type or its bytes, never a substring.
/// </summary>
public sealed class SubtitleProviderHardeningTests
{
    [Fact]
    public async Task The_opensubtitles_key_goes_to_its_api_and_not_to_the_host_serving_the_file()
    {
        var handler = new RecordingHandler();
        var provider = new OpenSubtitlesProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.opensubtitles.example/api/v1/") },
            new SubtitleProviderOptions { ApiKey = "the-key" },
            NullLogger<OpenSubtitlesProvider>.Instance);

        await provider.SearchAsync(new SubtitleProviderQuery("Movie.2024.1080p", "en", HearingImpaired: false));
        await provider.DownloadAsync("42");

        var search = Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/subtitles", StringComparison.Ordinal));
        var link = Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal));
        var file = Assert.Single(handler.Requests, r => r.Uri.Host == "files.example");
        Assert.Equal("the-key", search.ApiKey);
        Assert.Equal("the-key", link.ApiKey);
        Assert.Null(file.ApiKey);
    }

    [Theory]
    [InlineData("The.Assassin.srt", null, SubtitleFormat.Srt)]
    [InlineData("Movie.Subbed.srt", null, SubtitleFormat.Srt)]
    [InlineData("Movie.ass", null, SubtitleFormat.Ass)]
    [InlineData("Movie.ssa", null, SubtitleFormat.Ass)]
    [InlineData("Movie.vtt", null, SubtitleFormat.Vtt)]
    [InlineData("Movie.srt", "ass", SubtitleFormat.Ass)]
    [InlineData("Movie.2024.1080p.WEB-DL", null, SubtitleFormat.Srt)] // nothing to go on: the bytes decide
    public void A_listed_file_is_read_by_its_declared_format_or_its_extension(string name, string? format, SubtitleFormat expected)
    {
        var candidate = Assert.Single(Parse(name, format));

        Assert.Equal(expected, candidate.Format);
    }

    [Theory]
    [InlineData("Movie.sub", null)]
    [InlineData("Movie.idx", null)]
    [InlineData("Movie.srt", "sub")]
    public void A_listed_file_in_a_format_this_module_cannot_serve_is_skipped(string name, string? format)
    {
        Assert.Empty(Parse(name, format));
    }

    [Fact]
    public void A_download_is_known_by_its_bytes()
    {
        Assert.Equal(SubtitleFormat.Srt, SubdlSubtitleParser.FormatOfContent(
            Encoding.UTF8.GetBytes("﻿1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n")));
        Assert.Equal(SubtitleFormat.Vtt, SubdlSubtitleParser.FormatOfContent(
            Encoding.UTF8.GetBytes("WEBVTT\n\n00:01.000 --> 00:02.000\nHello\n")));
        Assert.Equal(SubtitleFormat.Ass, SubdlSubtitleParser.FormatOfContent(
            Encoding.UTF8.GetBytes("[Script Info]\nTitle: x\n")));

        // UTF-16 with its byte-order mark, as older Windows tools write SubRip.
        var utf16 = Encoding.Unicode;
        Assert.Equal(SubtitleFormat.Srt, SubdlSubtitleParser.FormatOfContent(
            [.. utf16.GetPreamble(), .. utf16.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nHola\r\n")]));

        // MicroDVD text and VobSub binary both travel as .sub; neither can be served as SubRip.
        Assert.Null(SubdlSubtitleParser.FormatOfContent(Encoding.UTF8.GetBytes("{1}{25}Hello\n{26}{50}World\n")));
        Assert.Null(SubdlSubtitleParser.FormatOfContent([0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01]));
    }

    private static IReadOnlyList<ProviderCandidate> Parse(string name, string? format)
    {
        var formatProperty = format is null ? string.Empty : $"\"format\": \"{format}\",";
        using var document = JsonDocument.Parse($$"""
            {
              "status": true,
              "subtitles": [
                {
                  "release_name": "Movie.2024",
                  "name": "Movie.zip",
                  "url": "/subtitle/1-2.zip",
                  "unpack_files": [
                    { "name": "{{name}}", {{formatProperty}} "url": "/subtitle/parent/file1" }
                  ]
                }
              ]
            }
            """);

        return SubdlSubtitleParser.Parse(document.RootElement, new SubtitleProviderQuery("Movie.2024", "en", HearingImpaired: false));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.TryGetValues("Api-Key", out var values) ? values.Single() : null;
            Requests.Add((request.RequestUri!, key));

            var body = request.RequestUri!.AbsolutePath switch
            {
                var path when path.EndsWith("/download", StringComparison.Ordinal) => """{ "link": "https://files.example/abc/sub.srt" }""",
                var path when path.EndsWith("/subtitles", StringComparison.Ordinal) => """{ "data": [] }""",
                _ => "1\n00:00:01,000 --> 00:00:02,000\nHello\n",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
