using Cinomni.Decision.Contracts;
using Cinomni.Decision.Persistence;
using Cinomni.Kernel.Identifiers;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.Decision;

/// <summary>
/// Builds the seed acquisition profiles — one per content kind. Both carry a sensible ordered set of
/// allowed qualities and a couple of custom formats, with no minimum format score (formats only
/// influence ranking for the MVP).
/// </summary>
internal static class DefaultProfile
{
    /// <summary>Below this an "episode" is a sample, a subtitle pack or a trailer, not content.</summary>
    private const long MinEpisodeBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Good enough by default: Bluray 1080p, the rank a household is unlikely to want bettered
    /// automatically. Everything above it in the allowed set (2160p) is still selected when it is what
    /// a search turns up first — the cutoff stops the *hunt*, it does not cap the quality.
    /// </summary>
    private const int DefaultCutoffRank = 50;

    /// <summary>The movie profile. Its name is unchanged so an existing install keeps its identity.</summary>
    public static AcquisitionProfile CreateMovieDefault()
    {
        var profile = NewProfile("Default", ProfileScope.Movie);
        AddStandardQualities(profile, minBytes: null);
        AddStandardRules(profile);
        return profile;
    }

    /// <summary>
    /// The series profile. It deliberately sets <b>no maximum size</b>: a season pack is N times an
    /// episode and the profile cannot know N, so any per-episode ceiling would silently reject every
    /// pack. The minimum is the junk filter that a movie-shaped ceiling could never be.
    /// </summary>
    public static AcquisitionProfile CreateSeriesDefault()
    {
        var profile = NewProfile("Default (Series)", ProfileScope.Series);
        AddStandardQualities(profile, minBytes: MinEpisodeBytes);
        AddStandardRules(profile);

        // A pack satisfies several goals in one download, so it is worth ranking above a single
        // episode of equal quality even before coverage breaks the tie.
        AddRule(profile, "Season Pack", score: 5, FormatConditionType.ReleaseType, nameof(ReleaseType.SeasonPack));
        return profile;
    }

    private static AcquisitionProfile NewProfile(string name, string appliesTo) => new()
    {
        Id = Uuid7.New(),
        Name = name,
        AppliesTo = appliesTo,
        MinFormatScore = 0,
        CutoffRank = DefaultCutoffRank,
        UpgradesAllowed = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static void AddStandardQualities(AcquisitionProfile profile, long? minBytes)
    {
        AddQuality(profile, QualitySource.Bluray, QualityResolution.R2160p, rank: 60, minBytes);
        AddQuality(profile, QualitySource.WebDl, QualityResolution.R2160p, rank: 55, minBytes);
        AddQuality(profile, QualitySource.Bluray, QualityResolution.R1080p, rank: 50, minBytes);
        AddQuality(profile, QualitySource.WebDl, QualityResolution.R1080p, rank: 45, minBytes);
        AddQuality(profile, QualitySource.WebRip, QualityResolution.R1080p, rank: 40, minBytes);
        AddQuality(profile, QualitySource.Bluray, QualityResolution.R720p, rank: 30, minBytes);
        AddQuality(profile, QualitySource.WebDl, QualityResolution.R720p, rank: 25, minBytes);
        AddQuality(profile, QualitySource.Hdtv, QualityResolution.R1080p, rank: 15, minBytes);
        AddQuality(profile, QualitySource.Hdtv, QualityResolution.R720p, rank: 10, minBytes);
    }

    private static void AddStandardRules(AcquisitionProfile profile)
    {
        AddRule(profile, "Remux", score: 50, FormatConditionType.QualityModifier, "Remux");
        AddRule(profile, "HEVC", score: 10, FormatConditionType.ReleaseTitle, @"\b(x265|h265|hevc)\b");
    }

    private static void AddQuality(
        AcquisitionProfile profile,
        QualitySource source,
        QualityResolution resolution,
        int rank,
        long? minBytes) =>
        profile.AllowedQualities.Add(new AllowedQuality
        {
            Id = Uuid7.New(),
            ProfileId = profile.Id,
            Source = source,
            Resolution = resolution,
            Rank = rank,
            MinSizeBytes = minBytes,
        });

    private static void AddRule(AcquisitionProfile profile, string name, int score, FormatConditionType type, string value)
    {
        var rule = new FormatRule { Id = Uuid7.New(), ProfileId = profile.Id, Name = name, Score = score };
        rule.Conditions.Add(new FormatCondition
        {
            Id = Uuid7.New(),
            RuleId = rule.Id,
            Type = type,
            Value = value,
            Negate = false,
            Required = false,
        });
        profile.FormatRules.Add(rule);
    }
}
