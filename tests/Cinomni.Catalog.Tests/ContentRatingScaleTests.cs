using Cinomni.Catalog.Contracts;

namespace Cinomni.Catalog.Tests;

public sealed class ContentRatingScaleTests
{
    [Fact]
    public void A_ceiling_hides_only_what_ranks_above_it()
    {
        Assert.Equal(["13", "16", "18", "X"], ContentRatingScale.Above("ES", "12", "ES"));
        Assert.Equal(["R", "TV-MA", "NC-17"], ContentRatingScale.Above("US", "TV-14", "US"));
        Assert.Equal(["X"], ContentRatingScale.Above("ES", "18", "ES"));
    }

    [Theory]
    [InlineData("Ai", "A")]
    [InlineData("7i", "7")]
    [InlineData("Infantil", "TP")]
    public void The_childrens_variants_rank_with_their_plain_label(string variant, string plain) =>
        Assert.Equal(ContentRatingScale.Above("ES", plain, "ES"), ContentRatingScale.Above("ES", variant, "ES"));

    [Fact]
    public void A_ceiling_for_another_region_does_not_apply()
    {
        Assert.False(ContentRatingScale.Applies("US", "12", "ES"));
        Assert.Empty(ContentRatingScale.Above("US", "12", "ES"));
    }

    [Fact]
    public void An_unknown_region_has_no_ladder_to_guess_from()
    {
        Assert.False(ContentRatingScale.HasLadder("SE"));
        Assert.Empty(ContentRatingScale.Certificates("SE"));
        Assert.False(ContentRatingScale.TryCanonical("SE", "11", out _));
    }

    [Fact]
    public void A_certificate_is_stored_in_the_ladders_own_spelling()
    {
        Assert.True(ContentRatingScale.TryCanonical("us", "pg-13", out var canonical));
        Assert.Equal("PG-13", canonical);
    }
}
