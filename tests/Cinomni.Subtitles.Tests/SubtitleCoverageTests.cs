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

    [Fact]
    public void The_stamp_ignores_language_order()
    {
        var left = new SubtitlePreference { WantedLanguages = ["es", "en"], Forced = true };
        var right = new SubtitlePreference { WantedLanguages = ["EN", "es"], Forced = true };

        Assert.Equal(SubtitleCoverage.Stamp(left), SubtitleCoverage.Stamp(right));
    }
}
