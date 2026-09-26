using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Planning searches for titles we already have. The sweep's whole discipline exists because every
/// request fans out to every indexer, so an upgrade pass has to be the stingiest thing in the system:
/// it asks only for what somebody judged worth bettering, and far less often than a gap is chased.
/// </summary>
public sealed class UpgradeSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plans_nothing_for_a_present_title_nobody_marked()
    {
        // The default state of every target in an existing installation. Searching them would mean the
        // first sweep after an update hammering every indexer with the entire library.
        var movie = Movie(missing: false, upgradeWanted: false);

        Assert.Empty(SweepPlanner.PlanForWork([movie], Now, quota: 10));
    }

    [Fact]
    public void Plans_a_search_for_a_present_title_worth_bettering()
    {
        var movie = Movie(missing: false, upgradeWanted: true);

        var planned = Assert.Single(SweepPlanner.PlanForWork([movie], Now, quota: 10));

        Assert.Equal(movie.Id, planned.Target.Id);
        Assert.Equal([movie.TargetRef], planned.UnitIds);
    }

    [Fact]
    public void Waits_far_longer_between_upgrade_searches_than_between_missing_ones()
    {
        // A gap is urgent; a better copy of something already watchable is not. Re-asking on the missing
        // cadence would multiply indexer traffic by the size of the library, forever.
        var recentlySearched = Movie(missing: false, upgradeWanted: true);
        recentlySearched.LastSearchRequestedAt = Now - SearchGranularityPolicy.SearchCooldown - TimeSpan.FromMinutes(1);

        Assert.Empty(SweepPlanner.PlanForWork([recentlySearched], Now, quota: 10));

        recentlySearched.LastSearchRequestedAt = Now - SearchGranularityPolicy.UpgradeCooldown - TimeSpan.FromMinutes(1);
        Assert.Single(SweepPlanner.PlanForWork([recentlySearched], Now, quota: 10));
    }

    [Fact]
    public void Never_plans_an_upgrade_for_an_unmonitored_target()
    {
        var movie = Movie(missing: false, upgradeWanted: true);
        movie.Monitored = false;

        Assert.Empty(SweepPlanner.PlanForWork([movie], Now, quota: 10));
    }

    [Fact]
    public void A_missing_title_is_still_chased_before_an_upgrade()
    {
        // With one slot left, the copy nobody can watch wins over the one that could be sharper.
        var missing = Movie(missing: true, upgradeWanted: false);
        var upgradable = Movie(missing: false, upgradeWanted: true);

        var planned = Assert.Single(SweepPlanner.PlanForWork([upgradable, missing], Now, quota: 1));

        Assert.Equal(missing.Id, planned.Target.Id);
    }

    [Fact]
    public void Plans_an_upgrade_for_an_episode_that_is_present_but_bettered()
    {
        var episode = Episode(seasonNumber: 1, episodeNumber: 3, missing: false, upgradeWanted: true);

        var planned = Assert.Single(SweepPlanner.PlanForWork([episode], Now, quota: 10));

        Assert.Equal(episode.Id, planned.Target.Id);
    }

    [Fact]
    public void Does_not_fold_upgradable_episodes_into_a_season_pack()
    {
        // The pack rule exists to close a season with one download. Re-fetching a whole season because
        // two episodes could be sharper is a different trade, and a much worse one.
        var present = Episode(1, 1, missing: false, upgradeWanted: true);
        var alsoPresent = Episode(1, 2, missing: false, upgradeWanted: true);
        var third = Episode(1, 3, missing: false, upgradeWanted: true);
        var season = Season(1);

        var plans = SweepPlanner.PlanForWork([season, present, alsoPresent, third], Now, quota: 10);

        Assert.Equal(3, plans.Count);
        Assert.DoesNotContain(plans, p => p.Target.Kind == TargetKind.Season);
    }

    private static MonitoredTarget Movie(bool missing, bool upgradeWanted) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Kind = TargetKind.Movie,
        TargetRef = Guid.NewGuid(),
        Monitored = true,
        Mode = MonitoringMode.All,
        IsMissing = missing,
        UpgradeWanted = upgradeWanted,
        CreatedAt = Now,
    };

    private static MonitoredTarget Episode(int seasonNumber, int episodeNumber, bool missing, bool upgradeWanted) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Kind = TargetKind.Episode,
        TargetRef = Guid.NewGuid(),
        Monitored = true,
        Mode = MonitoringMode.All,
        IsMissing = missing,
        UpgradeWanted = upgradeWanted,
        SeasonNumber = seasonNumber,
        EpisodeNumber = episodeNumber,
        AirDate = Now - TimeSpan.FromDays(365),
        CreatedAt = Now,
    };

    private static MonitoredTarget Season(int number) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        Kind = TargetKind.Season,
        TargetRef = Guid.NewGuid(),
        Monitored = true,
        Mode = MonitoringMode.All,
        IsMissing = false,
        SeasonNumber = number,
        CreatedAt = Now,
    };
}
