using System.Net;
using System.Text;
using Cinomni.Subtitles.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// The search address OpenSubtitles accepts. Its API answers anything but its one canonical form —
/// parameters in alphabetical order, values in lowercase, a space as <c>+</c>, an IMDb id as a bare
/// number — with a 301 to that form, and the provider client follows no redirect. So every search
/// failed against the real API while every test, which never sees the redirect, passed. The expected
/// addresses here are the ones the live API answered without redirecting.
/// </summary>
public sealed class OpenSubtitlesQueryTests
{
    [Fact]
    public async Task A_movie_is_searched_with_its_parameters_sorted_and_its_release_name_lowercased()
    {
        var query = await SearchQueryFor(new SubtitleProviderQuery(
            "28.Days.Later.2002.2160p.UHD.BluRay.REMUX.DV.P7.HDR.MULTi[Ben The Men]", "en", HearingImpaired: false));

        Assert.Equal(
            "?languages=en&query=28.days.later.2002.2160p.uhd.bluray.remux.dv.p7.hdr.multi%5Bben+the+men%5D",
            query);
    }

    [Fact]
    public async Task An_episode_is_searched_in_the_canonical_form_with_a_bare_imdb_number_and_a_lowercase_language()
    {
        var query = await SearchQueryFor(new SubtitleProviderQuery(
            "The.Big.Bang.Theory.S11E04.1080p", "pt-BR", HearingImpaired: false,
            SeriesTitle: "The Big Bang Theory", SeasonNumber: 11, EpisodeNumber: 4, ParentImdbId: "tt0898266"));

        Assert.Equal(
            "?episode_number=4&languages=pt-br&parent_imdb_id=898266&query=the+big+bang+theory&season_number=11",
            query);
    }

    [Theory]
    [InlineData("tt0898266", "898266")]
    [InlineData("0898266", "898266")]
    [InlineData("898266", "898266")]
    [InlineData("TT0898266", "898266")]
    public async Task The_imdb_id_loses_its_prefix_and_leading_zeros(string catalogued, string sent)
    {
        var query = await SearchQueryFor(new SubtitleProviderQuery(
            "Show.S01E01", "en", HearingImpaired: false,
            SeriesTitle: "Show", SeasonNumber: 1, EpisodeNumber: 1, ParentImdbId: catalogued));

        Assert.Contains($"&parent_imdb_id={sent}&", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tt")]
    [InlineData("tt0000000")]
    [InlineData("nm0000158")]
    [InlineData("tt12&x=1")]
    public async Task An_imdb_id_that_is_not_a_title_number_is_left_out(string catalogued)
    {
        var query = await SearchQueryFor(new SubtitleProviderQuery(
            "Show.S01E01", "en", HearingImpaired: false,
            SeriesTitle: "Show", SeasonNumber: 1, EpisodeNumber: 1, ParentImdbId: catalogued));

        Assert.DoesNotContain("parent_imdb_id", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Characters_that_would_change_the_query_are_escaped_after_lowercasing()
    {
        var query = await SearchQueryFor(new SubtitleProviderQuery("Amélie & Co+1 = L'École", "en", HearingImpaired: false));

        Assert.Equal("?languages=en&query=am%C3%A9lie+%26+co%2B1+%3D+l%27%C3%A9cole", query);
    }

    private static async Task<string> SearchQueryFor(SubtitleProviderQuery query)
    {
        var handler = new RecordingHandler();
        var provider = new OpenSubtitlesProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.opensubtitles.example/api/v1/") },
            new SubtitleProviderOptions { ApiKey = "the-key" },
            NullLogger<OpenSubtitlesProvider>.Instance);

        await provider.SearchAsync(query);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/subtitles", sent.AbsolutePath);
        return sent.Query;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": [] }""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
