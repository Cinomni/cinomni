using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;
using Cinomni.Metadata.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// What the TMDB adapter makes of genres and age classifications, against scripted responses.
/// <para>
/// The classification cases are the ones worth writing down, because every way of getting this wrong
/// is silent. A rating read from the wrong region classifies a library under rules that do not apply
/// to it; a blank certification stored as though it were a rating makes a work look classified when
/// the provider said it did not know. Neither fails — they both just answer wrongly.
/// </para>
/// </summary>
public sealed class TmdbClassificationTests
{
    private const string BaseAddress = "https://tmdb.example/";

    /// <summary>A movie whose certification differs by country, including a country that left it blank.</summary>
    private const string MovieDetail = """
        {
          "id": 603,
          "title": "The Matrix",
          "original_title": "The Matrix",
          "release_date": "1999-03-30",
          "runtime": 136,
          "original_language": "en",
          "genres": [
            { "id": 28, "name": "Action" },
            { "id": 878, "name": "Science Fiction" },
            { "id": 28, "name": "action" }
          ],
          "release_dates": {
            "results": [
              { "iso_3166_1": "US", "release_dates": [ { "certification": "R" } ] },
              { "iso_3166_1": "ES", "release_dates": [ { "certification": "" }, { "certification": "18" } ] },
              { "iso_3166_1": "DE", "release_dates": [ { "certification": "16" } ] }
            ]
          }
        }
        """;

    private const string TvDetail = """
        {
          "id": 3,
          "name": "Frontier",
          "original_name": "Frontier",
          "first_air_date": "2016-11-06",
          "original_language": "en",
          "genres": [ { "id": 18, "name": "Drama" } ],
          "content_ratings": {
            "results": [
              { "iso_3166_1": "US", "rating": "TV-MA" },
              { "iso_3166_1": "ES", "rating": "16" }
            ]
          }
        }
        """;

    private readonly FakeHttpMessageHandler _handler = new FakeHttpMessageHandler()
        .Respond("movie/603?", MovieDetail)
        .Respond("tv/3?", TvDetail);

    [Fact]
    public async Task Genres_come_through_in_the_providers_order_without_repeats()
    {
        var result = await CreateSource(region: "US").FetchAsync("603", MetadataMediaKind.Movie);

        Assert.NotNull(result);
        // The provider's own order and its own names — no mapping to a vocabulary of ours, because
        // two providers disagreeing about "Sci-Fi & Fantasy" versus "Science Fiction" is not a
        // disagreement this layer can resolve. The duplicate differs only in case and is dropped.
        Assert.Equal(["Action", "Science Fiction"], result.Genres);
    }

    [Theory]
    [InlineData("US", "R")]
    [InlineData("DE", "16")]
    // Spain lists a blank certification before the real one: TMDB does this routinely, and a blank is
    // the provider saying it does not know rather than a rating of its own.
    [InlineData("ES", "18")]
    public async Task The_classification_is_the_one_for_the_configured_region(string region, string expected)
    {
        var result = await CreateSource(region).FetchAsync("603", MetadataMediaKind.Movie);

        Assert.Equal(expected, result?.ContentRating);
    }

    [Fact]
    public async Task A_series_reads_its_classification_from_the_other_shape_tmdb_uses()
    {
        // Movies and series are not shaped alike: a series carries one rating per country, a movie a
        // list of them per country, because a film can be certified for cinema and again for release.
        var result = await CreateSource(region: "ES").FetchAsync("3", MetadataMediaKind.Series);

        Assert.Equal("16", result?.ContentRating);
        Assert.Equal(["Drama"], result?.Genres);
    }

    [Fact]
    public async Task An_installation_that_named_no_region_reads_no_classification()
    {
        var result = await CreateSource(region: string.Empty).FetchAsync("603", MetadataMediaKind.Movie);

        // The shipped state, and it has to stay silent rather than pick whichever country the provider
        // listed first. A wrong region does not fail — it classifies a whole library under rules that
        // do not apply to it, and anything built on that would enforce the wrong thing convincingly.
        Assert.NotNull(result);
        Assert.Null(result.ContentRating);
        // Genres are not regional, so they arrive either way.
        Assert.NotEmpty(result.Genres!);
    }

    [Fact]
    public async Task A_region_the_provider_does_not_classify_for_reads_as_unrated()
    {
        var result = await CreateSource(region: "SE").FetchAsync("603", MetadataMediaKind.Movie);

        // Unrated is ordinary and must stay watchable: a parental control that blocked the unrated by
        // default would hide most of a library on the day somebody turned it on.
        Assert.Null(result?.ContentRating);
    }

    private TmdbMetadataSource CreateSource(string region) => new(
        _handler.CreateClient(BaseAddress),
        new TmdbProviderOptions { ApiKey = "fake-key", BaseAddress = BaseAddress },
        new DisabledProviderNotice(NullLogger<DisabledProviderNotice>.Instance),
        NullLogger<TmdbMetadataSource>.Instance,
        new FixedLiveOptions<ContentRatingOptions>(new ContentRatingOptions { Region = region }));
}
