using Cinomni.Metadata.Contracts;

namespace Cinomni.Metadata.Tests;

/// <summary>Pure unit tests for deriving a smaller rendition of a provider artwork url.</summary>
public sealed class ArtworkVariantsTests
{
    private const string Original = "https://image.tmdb.org/t/p/original/abc123.jpg";

    [Theory]
    [InlineData(ArtworkSize.PosterSmall, "https://image.tmdb.org/t/p/w342/abc123.jpg")]
    [InlineData(ArtworkSize.BackdropSmall, "https://image.tmdb.org/t/p/w780/abc123.jpg")]
    [InlineData(ArtworkSize.BackdropLarge, "https://image.tmdb.org/t/p/w1280/abc123.jpg")]
    public void A_tmdb_original_is_resized(ArtworkSize size, string expected)
    {
        Assert.Equal(expected, ArtworkVariants.Resize(Original, size));
    }

    [Fact]
    public void A_tmdb_url_already_sized_is_resized_too()
    {
        Assert.Equal(
            "https://image.tmdb.org/t/p/w342/abc123.jpg",
            ArtworkVariants.Resize("https://IMAGE.tmdb.org/t/p/w500/abc123.jpg", ArtworkSize.PosterSmall));
    }

    [Theory]
    // Other providers publish no size ladder this can address: their url passes through untouched.
    [InlineData("https://artworks.thetvdb.com/banners/posters/81189-1.jpg")]
    [InlineData("https://static.tvmaze.com/uploads/images/original_untouched/1/2.jpg")]
    // Look-alikes of the TMDB layout that are not TMDB, or not its image path.
    [InlineData("https://image.tmdb.org.evil.example/t/p/original/abc123.jpg")]
    [InlineData("http://image.tmdb.org/t/p/original/abc123.jpg")]
    [InlineData("https://image.tmdb.org/t/p/../original/abc123.jpg")]
    [InlineData("https://image.tmdb.org/t/p/original/")]
    [InlineData("https://image.tmdb.org/other/original/abc123.jpg")]
    [InlineData("https://image.tmdb.org/t/p/huge/abc123.jpg")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Anything_else_passes_through_unchanged(string url)
    {
        Assert.Equal(url, ArtworkVariants.Resize(url, ArtworkSize.PosterSmall));
    }

    [Fact]
    public void No_url_stays_no_url()
    {
        Assert.Null(ArtworkVariants.Resize(null, ArtworkSize.PosterSmall));
    }
}
