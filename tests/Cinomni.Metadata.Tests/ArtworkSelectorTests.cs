using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Providers;

namespace Cinomni.Metadata.Tests;

/// <summary>Pure unit tests for the artwork selection policy (no database, no I/O).</summary>
public sealed class ArtworkSelectorTests
{
    private static readonly ArtworkSelectionOptions English = ArtworkSelectionOptions.ForLanguage("en-US");
    private static readonly ArtworkSelectionOptions Spanish = ArtworkSelectionOptions.ForLanguage("es-ES");

    private static ProviderArtwork Poster(string url, string? language, double? vote = null, int? votes = null, int? w = null, int? h = null) =>
        new(ArtworkKind.Poster, url, language, w, h, vote, votes);

    [Fact]
    public void Preferred_language_beats_a_higher_vote_in_another_language()
    {
        var candidates = new[]
        {
            Poster("es", "es", vote: 9.0),
            Poster("en", "en", vote: 6.0),
        };

        var selection = ArtworkSelector.Select(candidates, English);

        Assert.Equal("en", selection.PosterUrl);
    }

    [Fact]
    public void Language_preference_follows_the_configured_language()
    {
        var candidates = new[]
        {
            Poster("es", "es", vote: 6.0),
            Poster("en", "en", vote: 9.0),
        };

        var selection = ArtworkSelector.Select(candidates, Spanish);

        Assert.Equal("es", selection.PosterUrl);
    }

    [Fact]
    public void Neutral_artwork_is_preferred_over_a_non_preferred_language()
    {
        var candidates = new[]
        {
            Poster("fr", "fr", vote: 9.0),
            Poster("neutral", null, vote: 5.0),
        };

        var selection = ArtworkSelector.Select(candidates, English);

        Assert.Equal("neutral", selection.PosterUrl);
    }

    [Fact]
    public void Within_a_language_higher_vote_average_wins_then_count_then_resolution()
    {
        var candidates = new[]
        {
            Poster("low", "en", vote: 7.0, votes: 100, w: 4000, h: 6000),
            Poster("high", "en", vote: 8.5, votes: 10, w: 500, h: 750),
            Poster("mid", "en", vote: 8.5, votes: 200, w: 500, h: 750),
        };

        var selection = ArtworkSelector.Select(candidates, English);

        Assert.Equal("mid", selection.PosterUrl); // same top vote average, more votes
    }

    [Fact]
    public void Resolution_breaks_a_full_tie()
    {
        var candidates = new[]
        {
            Poster("small", "en", vote: 8.0, votes: 50, w: 1000, h: 1500),
            Poster("large", "en", vote: 8.0, votes: 50, w: 2000, h: 3000),
        };

        var selection = ArtworkSelector.Select(candidates, English);

        Assert.Equal("large", selection.PosterUrl);
    }

    [Fact]
    public void Each_kind_is_selected_independently()
    {
        var candidates = new ProviderArtwork[]
        {
            Poster("poster", "en"),
            new(ArtworkKind.Backdrop, "backdrop", null, 3840, 2160, 9.0, 10),
            new(ArtworkKind.Logo, "logo", "en", 800, 310, 7.0, 5),
        };

        var selection = ArtworkSelector.Select(candidates, English);

        Assert.Equal("poster", selection.PosterUrl);
        Assert.Equal("backdrop", selection.BackdropUrl);
        Assert.Equal("logo", selection.Logo?.Url);
    }

    [Fact]
    public void No_candidates_of_a_kind_selects_nothing_for_it()
    {
        var candidates = new[] { Poster("poster", "en") };

        var selection = ArtworkSelector.Select(candidates, English);

        Assert.Equal("poster", selection.PosterUrl);
        Assert.Null(selection.BackdropUrl);
        Assert.Null(selection.Logo);
    }

    [Fact]
    public void Empty_set_selects_nothing()
    {
        var selection = ArtworkSelector.Select([], English);

        Assert.Null(selection.PosterUrl);
        Assert.Null(selection.BackdropUrl);
    }

    [Fact]
    public void Selected_candidate_is_identified_by_reference()
    {
        var chosen = Poster("en", "en", vote: 8.0);
        var candidates = new[] { chosen, Poster("es", "es", vote: 9.0) };

        var selection = ArtworkSelector.Select(candidates, English);

        Assert.True(selection.IsSelected(chosen));
        Assert.False(selection.IsSelected(candidates[1]));
    }
}
