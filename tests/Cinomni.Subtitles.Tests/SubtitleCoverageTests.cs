using Cinomni.Subtitles.Application;

namespace Cinomni.Subtitles.Tests;

public sealed class SubtitleCoverageTests
{
    [Fact]
    public void An_english_track_does_not_satisfy_a_hearing_impaired_request()
    {
        var present = new[] { ("en", false, false) };

        Assert.True(SubtitleCoverage.IsSatisfied("en", false, false, present));
        Assert.False(SubtitleCoverage.IsSatisfied("en", false, true, present));
        Assert.False(SubtitleCoverage.IsSatisfied("es", false, false, present));
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("de", "ger")]
    [InlineData("de", "deu")]
    [InlineData("pt", "POR")]
    [InlineData("en", "en")]
    public void An_embedded_track_tagged_in_three_letters_satisfies_the_two_letter_preference(string wanted, string tagged)
    {
        // Containers tag tracks in ISO 639-2; compared literally, no embedded subtitle ever counted.
        var present = new[] { (tagged, false, false) };

        Assert.True(SubtitleCoverage.IsSatisfied(wanted, false, false, present));
    }

    [Theory]
    [InlineData("en", "spa")]
    [InlineData("en", "")]
    [InlineData("es", "eng")]
    public void A_track_in_another_language_does_not(string wanted, string tagged)
    {
        Assert.False(SubtitleCoverage.IsSatisfied(wanted, false, false, [(tagged, false, false)]));
    }

    [Fact]
    public void The_stamp_ignores_language_order()
    {
        var left = new SubtitlePreference { WantedLanguages = ["es", "en"], Forced = true };
        var right = new SubtitlePreference { WantedLanguages = ["EN", "es"], Forced = true };

        Assert.Equal(SubtitleCoverage.Stamp(left), SubtitleCoverage.Stamp(right));
    }
}
