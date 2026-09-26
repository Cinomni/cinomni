using System.Text.Json;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;

namespace Cinomni.Metadata.Tests;

public sealed class TmdbTrendingParserTests
{
    [Fact]
    public void A_movie_hit_keeps_the_id_title_and_year_and_stops_at_the_limit()
    {
        using var document = JsonDocument.Parse(
            """
            {"results":[
              {"id":11,"title":"Inception","release_date":"2010-07-16"},
              {"id":12,"title":"Second","release_date":"2011-01-01"},
              {"title":"No id"},
              {"id":13}
            ]}
            """);

        var titles = TmdbTrendingParser.Parse(document.RootElement, MetadataMediaKind.Movie, limit: 1);

        var only = Assert.Single(titles);
        Assert.Equal("tmdb", only.Provider);
        Assert.Equal("11", only.ExternalId);
        Assert.Equal("Inception", only.Title);
        Assert.Equal(2010, only.Year);
        Assert.Equal(MetadataMediaKind.Movie, only.Kind);
    }

    [Fact]
    public void A_payload_that_is_not_a_list_yields_nothing()
    {
        using var document = JsonDocument.Parse("""{"results":"nope"}""");

        var titles = TmdbTrendingParser.Parse(document.RootElement, MetadataMediaKind.Series, limit: 20);

        Assert.Empty(titles);
    }
}
