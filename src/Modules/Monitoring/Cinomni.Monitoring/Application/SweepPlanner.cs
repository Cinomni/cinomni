using Cinomni.Monitoring.Contracts;
using Cinomni.Monitoring.Persistence;

namespace Cinomni.Monitoring.Application;

/// <summary>
/// One search the sweep decided to request: which target carries it, which catalog units it is trying to
/// acquire, and which targets get their <c>LastSearchRequestedAt</c> stamped when it goes out.
/// <para>
/// <see cref="Stamp"/> is the duplicate-download suppressor and it uses only the column that already
/// exists. A season-level search stamps the season <em>and every episode inside it</em>, so the same
/// sweep — and every sweep for the whole cooldown — cannot also ask for those episodes one by one and
/// have Decision select the very same pack N times.
/// </para>
/// </summary>
internal sealed record PlannedSearch(
    MonitoredTarget Target,
    IReadOnlyList<Guid> UnitIds,
    IReadOnlyList<MonitoredTarget> Stamp);

/// <summary>
/// Turns one work's monitored targets into the searches the sweep should request. Pure: it reads the
/// rows and the clock it is given, and decides nothing about I/O.
/// </summary>
internal static class SweepPlanner
{
    /// <summary>
    /// Plans at most <paramref name="quota"/> searches for one work, movie roots first and then season by
    /// season in ascending order.
    /// </summary>
    public static List<PlannedSearch> PlanForWork(
        IReadOnlyList<MonitoredTarget> family,
        DateTimeOffset now,
        int quota,
        TimeSpan searchDelay = default)
    {
        var plans = new List<PlannedSearch>();
        if (quota <= 0)
        {
            return plans;
        }

        PlanMovies(family, now, quota, searchDelay, plans);
        PlanSeasons(family, now, quota, searchDelay, plans);
        return plans;
    }

    /// <summary>The movie path: one target, one search, its own unit id — whether it is missing or bettered.</summary>
    private static void PlanMovies(
        IReadOnlyList<MonitoredTarget> family,
        DateTimeOffset now,
        int quota,
        TimeSpan searchDelay,
        List<PlannedSearch> plans)
    {
        // Gaps before polish: with the last slot of a sweep, the film nobody can watch beats the one that
        // could be sharper.
        var due = family
            .Where(t => t.Kind == TargetKind.Movie && IsDueLeaf(t, now, searchDelay))
            .OrderByDescending(t => t.IsMissing);

        foreach (var movie in due)
        {
            if (plans.Count >= quota)
            {
                return;
            }

            plans.Add(new PlannedSearch(movie, [movie.TargetRef], [movie]));
        }
    }

    private static void PlanSeasons(
        IReadOnlyList<MonitoredTarget> family,
        DateTimeOffset now,
        int quota,
        TimeSpan searchDelay,
        List<PlannedSearch> plans)
    {
        var seasonTargets = SeasonTargetsByNumber(family);

        foreach (var group in EpisodesBySeason(family))
        {
            if (plans.Count >= quota)
            {
                return;
            }

            var episodes = group.Value;
            // An unaired episode is never searched. A missing one still inside the search delay is
            // treated the same way: it is neither a candidate nor part of the denominator that decides
            // whether the season is "mostly missing". An upgrade of something already held does not wait.
            var aired = episodes.FindAll(e => SearchGranularityPolicy.HasAired(e.AirDate, now));
            var searchable = episodes.FindAll(e => SearchGranularityPolicy.HasAired(e.AirDate, now, searchDelay));
            var missing = searchable.FindAll(e => e.Monitored && e.IsMissing);

            if (missing.Count > 0
                && seasonTargets.TryGetValue(group.Key, out var seasonTarget)
                && SearchGranularityPolicy.ShouldSearchAsPack(missing.Count, searchable.Count))
            {
                PlanPack(seasonTarget, episodes, missing, now, plans);
                continue;
            }

            PlanEpisodes(missing, now, quota, searchDelay, plans);

            // Episodes we hold and could hold better, always one by one. The pack rule buys a whole
            // season with one download when most of it is absent; re-fetching a season because a few of
            // its episodes could be sharper spends the same bandwidth for far less, and would replace
            // the episodes that were already fine.
            PlanEpisodes(aired.FindAll(e => e.Monitored && !e.IsMissing && e.UpgradeWanted), now, quota, searchDelay, plans);
        }
    }

    /// <summary>
    /// A mostly-missing season is fetched as one pack. If its own cooldown has not lapsed the season is
    /// skipped entirely rather than falling back to singles — falling back would defeat the suppression
    /// the pack search just bought.
    /// <para>
    /// The pack's cadence follows its <em>content</em>: a season holding a just-aired episode is asked for
    /// again after thirty minutes, exactly as that episode would be if it were searched on its own.
    /// Grading the pack by the season's premiere instead would leave the work due — the sweep selects it
    /// on its episodes — while producing no search, and one of the sweep's slots would go to that work on
    /// every single tick.
    /// </para>
    /// </summary>
    private static void PlanPack(
        MonitoredTarget seasonTarget,
        List<MonitoredTarget> episodes,
        List<MonitoredTarget> missing,
        DateTimeOffset now,
        List<PlannedSearch> plans)
    {
        var airsAt = LatestAirInstant(missing) ?? seasonTarget.AirDate;
        if (!SearchGranularityPolicy.IsDue(seasonTarget.LastSearchRequestedAt, airsAt, now))
        {
            return;
        }

        List<MonitoredTarget> stamp = [seasonTarget, .. episodes];
        plans.Add(new PlannedSearch(seasonTarget, missing.ConvertAll(e => e.TargetRef), stamp));
    }

    /// <summary>
    /// The most recent air instant among <paramref name="targets"/>, or null when none of them has one.
    /// The most recent instant is the one that earns the shortest cooldown.
    /// </summary>
    private static DateTimeOffset? LatestAirInstant(List<MonitoredTarget> targets)
    {
        DateTimeOffset? latest = null;
        foreach (var target in targets)
        {
            if (target.AirDate is { } instant && (latest is not { } current || instant > current))
            {
                latest = instant;
            }
        }

        return latest;
    }

    private static void PlanEpisodes(
        List<MonitoredTarget> candidates,
        DateTimeOffset now,
        int quota,
        TimeSpan searchDelay,
        List<PlannedSearch> plans)
    {
        foreach (var episode in candidates.Where(e => IsDueLeaf(e, now, searchDelay)))
        {
            if (plans.Count >= quota)
            {
                return;
            }

            plans.Add(new PlannedSearch(episode, [episode.TargetRef], [episode]));
        }
    }

    /// <summary>
    /// A leaf worth a search: either the content is missing, or it is present and somebody judged it
    /// worth bettering. The two are graded on different clocks — see
    /// <see cref="SearchGranularityPolicy.UpgradeCooldown"/> — and a missing target is never treated as
    /// an upgrade even if it happens to carry the flag from before its file was lost.
    /// </summary>
    private static bool IsDueLeaf(MonitoredTarget target, DateTimeOffset now, TimeSpan searchDelay)
    {
        if (!target.Monitored)
        {
            return false;
        }

        // A missing title waits out the delay. Something already on disk does not: the delay exists so
        // the first grab is not the first file posted, and an upgrade is not that grab.
        var aired = target.IsMissing
            ? SearchGranularityPolicy.HasAired(target.AirDate, now, searchDelay)
            : SearchGranularityPolicy.HasAired(target.AirDate, now);
        if (!aired)
        {
            return false;
        }

        return target.IsMissing
            ? SearchGranularityPolicy.IsDue(target.LastSearchRequestedAt, target.AirDate, now)
            : target.UpgradeWanted && SearchGranularityPolicy.IsUpgradeDue(target.LastSearchRequestedAt, now);
    }

    /// <summary>Season targets keyed by season number; a season with no number cannot carry a pack search.</summary>
    private static Dictionary<int, MonitoredTarget> SeasonTargetsByNumber(IReadOnlyList<MonitoredTarget> family)
    {
        var byNumber = new Dictionary<int, MonitoredTarget>();
        foreach (var season in family.Where(t => t.Kind == TargetKind.Season && t.SeasonNumber is not null))
        {
            byNumber.TryAdd(season.SeasonNumber!.Value, season);
        }

        return byNumber;
    }

    /// <summary>Episode targets grouped by season number, seasons ascending and episodes in order.</summary>
    private static SortedDictionary<int, List<MonitoredTarget>> EpisodesBySeason(IReadOnlyList<MonitoredTarget> family)
    {
        var bySeason = new SortedDictionary<int, List<MonitoredTarget>>();
        foreach (var episode in family.Where(t => t.Kind == TargetKind.Episode && t.SeasonNumber is not null))
        {
            if (!bySeason.TryGetValue(episode.SeasonNumber!.Value, out var episodes))
            {
                episodes = [];
                bySeason[episode.SeasonNumber!.Value] = episodes;
            }

            episodes.Add(episode);
        }

        foreach (var episodes in bySeason.Values)
        {
            episodes.Sort((left, right) => (left.EpisodeNumber ?? 0).CompareTo(right.EpisodeNumber ?? 0));
        }

        return bySeason;
    }
}
