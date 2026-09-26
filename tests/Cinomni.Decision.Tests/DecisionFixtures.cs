using Cinomni.Decision.Contracts;
using Cinomni.Decision.Evaluation;
using Cinomni.Decision.Persistence;
using Cinomni.Discovery.Contracts;
using Cinomni.Kernel.Identifiers;
using Cinomni.ReleaseParsing.Contracts;
using Cinomni.ReleaseParsing.Parsing;

namespace Cinomni.Decision.Tests;

/// <summary>Builders for the evaluation unit tests: parsed releases, candidates, profiles and requests.</summary>
internal static class DecisionFixtures
{
    private static readonly ReleaseParser Parser = new();

    public static ParsedRelease Parse(string title) => Parser.Parse(title).Value;

    public static ReleaseCandidate Candidate(string guid, string title, long size = 5_000_000_000, int? seeders = 10) =>
        new(guid, title, $"magnet:?xt=urn:btih:{guid}", ReleaseProtocol.Torrent, size, seeders, null, "Idx");

    /// <summary>What a search asked for — the context the identity checks judge a candidate against.</summary>
    public static EvaluationRequest Request(
        string term,
        string contentKind = "Episode",
        int? season = null,
        int? episode = null,
        int? absolute = null,
        DateOnly? airDate = null,
        IReadOnlyList<Guid>? covered = null,
        int? year = null) =>
        new(contentKind, term, season, episode, absolute, airDate, covered, year);

    /// <param name="cutoffRank">
    /// Defaults above every rank these tests use, so a test that does not care about the cutoff never
    /// trips over it — a test about the cutoff sets it.
    /// </param>
    public static AcquisitionProfile Profile(
        int minFormatScore = 0,
        int cutoffRank = 1000,
        bool upgradesAllowed = true) => new()
    {
        Id = Uuid7.New(),
        Name = "Test",
        MinFormatScore = minFormatScore,
        CutoffRank = cutoffRank,
        UpgradesAllowed = upgradesAllowed,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    public static AcquisitionProfile WithQuality(
        this AcquisitionProfile profile,
        QualitySource source,
        QualityResolution resolution,
        int rank,
        long? minBytes = null,
        long? maxBytes = null)
    {
        profile.AllowedQualities.Add(new AllowedQuality
        {
            Id = Uuid7.New(),
            ProfileId = profile.Id,
            Source = source,
            Resolution = resolution,
            Rank = rank,
            MinSizeBytes = minBytes,
            MaxSizeBytes = maxBytes,
        });
        return profile;
    }

    public static AcquisitionProfile WithRule(
        this AcquisitionProfile profile,
        string name,
        int score,
        FormatConditionType type,
        string value)
    {
        var rule = new FormatRule { Id = Uuid7.New(), ProfileId = profile.Id, Name = name, Score = score };
        rule.Conditions.Add(new FormatCondition
        {
            Id = Uuid7.New(),
            RuleId = rule.Id,
            Type = type,
            Value = value,
        });
        profile.FormatRules.Add(rule);
        return profile;
    }
}
