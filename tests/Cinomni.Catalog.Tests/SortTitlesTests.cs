using Cinomni.Catalog.Application;
using Xunit;

namespace Cinomni.Catalog.Tests;

/// <summary>Unit tests for sort-title normalization (no database).</summary>
public sealed class SortTitlesTests
{
    [Theory]
    [InlineData("The Matrix", "matrix")]
    [InlineData("A Beautiful Mind", "beautiful mind")]
    [InlineData("An Education", "education")]
    [InlineData("Avatar", "avatar")]
    [InlineData("  Inception  ", "inception")]
    [InlineData("Theater", "theater")] // leading "the" without a space is not an article
    public void Normalize_drops_leading_article_and_lowercases(string title, string expected)
    {
        Assert.Equal(expected, SortTitles.Normalize(title));
    }
}
