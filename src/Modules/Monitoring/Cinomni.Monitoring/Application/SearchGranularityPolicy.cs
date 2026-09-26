namespace Cinomni.Monitoring.Application;

/// <summary>
/// The rules that decide <em>how much</em> the missing sweep is allowed to ask for, and at what
/// granularity. These are constants, not configuration, exactly like the cooldown they sit beside — they
/// exist to keep the platform from being rate-limited or banned by real indexers, and a user cannot be
/// expected to tune them.
/// <para>
/// The numbers matter: every <c>SearchRequested</c> fans out to every enabled indexer, so a 250-episode
/// show with no quota would issue 250 × (indexer count) HTTP requests on a single 15-minute tick.
/// </para>
/// </summary>
public static class SearchGranularityPolicy
{
    /// <summary>Do not re-request a search for the same target more often than this.</summary>
    public static readonly TimeSpan SearchCooldown = TimeSpan.FromHours(6);

    /// <summary>
    /// Cooldown applied to an episode that aired very recently: the release usually appears within
    /// minutes of the broadcast, and waiting six hours for the next attempt is the difference between a
    /// same-evening download and a next-morning one.
    /// </summary>
    public static readonly TimeSpan RecentAirCooldown = TimeSpan.FromMinutes(30);

    /// <summary>How long after its air instant an episode still counts as "just aired".</summary>
    public static readonly TimeSpan RecentAirWindow = TimeSpan.FromDays(2);

    /// <summary>
    /// How long between searches for something we already hold and could hold better. Two orders of
    /// magnitude slower than chasing a gap, and deliberately: a missing episode is unwatchable and worth
    /// asking about every six hours, while a sharper copy of something already playing is worth asking
    /// about weekly. The library being swept is every title the household owns, not just the ones with
    /// holes, so this multiplier is what keeps upgrades from becoming the bulk of all indexer traffic.
    /// </summary>
    public static readonly TimeSpan UpgradeCooldown = TimeSpan.FromDays(7);

    /// <summary>A season is searched as a pack when at least this fraction of its aired episodes is missing.</summary>
    public const double SeasonPackMissingRatio = 0.6;

    /// <summary>...and at least this many episodes are missing (a 2-episode gap is cheaper to fetch singly).</summary>
    public const int SeasonPackMinMissingEpisodes = 3;

    /// <summary>Cap the searches published per sweep; the remainder is drained by the next scheduled tick.</summary>
    public const int MaxSearchesPerSweep = 200;

    /// <summary>
    /// ...and no single work may claim more than this many of them. Without it, one large show
    /// monopolises every sweep for hours and no other work is ever searched.
    /// </summary>
    public const int MaxSearchesPerWork = 10;

    /// <summary>How many works one sweep considers, derived from the two caps above.</summary>
    public const int MaxWorksPerSweep = MaxSearchesPerSweep / MaxSearchesPerWork;

    /// <summary>
    /// Whether a unit has aired, and the sweep's delay after that instant has elapsed.
    /// A target with no air instant at all (every movie, and an episode whose provider published no
    /// date) counts as aired: only a <em>known</em> instant can be waited past. A zero delay is today's
    /// behaviour — searchable the moment it has aired.
    /// </summary>
    public static bool HasAired(DateTimeOffset? airsAt, DateTimeOffset now, TimeSpan searchDelay = default) =>
        airsAt is not { } instant || instant <= now - (searchDelay < TimeSpan.Zero ? TimeSpan.Zero : searchDelay);

    /// <summary>
    /// The cooldown for one target. The unaired gate keeps an unaired episode out of the sweep
    /// entirely, so this only ever grades content that already exists.
    /// </summary>
    public static TimeSpan CooldownFor(DateTimeOffset? airsAt, DateTimeOffset now) =>
        airsAt is { } instant && instant <= now && now - instant <= RecentAirWindow
            ? RecentAirCooldown
            : SearchCooldown;

    /// <summary>
    /// Whether a target is due for another search under its own cooldown.
    /// <para>
    /// <c>EvaluateMissingCommandHandler.DueWorkIdsAsync</c> holds the SQL twin of this rule, because the
    /// sweep has to <em>select</em> works by the same test the planner then applies. The two must be kept
    /// in step: selecting on a shorter cooldown than the planner enforces means the sweep spends its
    /// bounded slots on works that produce no search at all, and the works behind them are never reached.
    /// </para>
    /// </summary>
    public static bool IsDue(DateTimeOffset? lastRequestedAt, DateTimeOffset? airsAt, DateTimeOffset now) =>
        lastRequestedAt is not { } last || last <= now - CooldownFor(airsAt, now);

    /// <summary>
    /// Whether a target we already hold is due another look for something better. Air date plays no part:
    /// the content exists and has been watchable for however long, so the just-aired urgency that grades
    /// a missing episode has nothing to say here.
    /// <para>
    /// <c>EvaluateMissingCommandHandler.DueWorkIdsAsync</c> holds this rule's SQL twin as well. The two
    /// must move together for the same reason as <see cref="IsDue"/>.
    /// </para>
    /// </summary>
    public static bool IsUpgradeDue(DateTimeOffset? lastRequestedAt, DateTimeOffset now) =>
        lastRequestedAt is not { } last || last <= now - UpgradeCooldown;

    /// <summary>
    /// Whether a season is missing enough of itself to be worth fetching as one pack. Both conditions must
    /// hold: the ratio keeps a nearly-complete season on single-episode searches, and the floor keeps a
    /// two-episode gap in a short season from pulling a whole pack down again.
    /// </summary>
    public static bool ShouldSearchAsPack(int missingCount, int airedCount) =>
        airedCount > 0
        && missingCount >= SeasonPackMinMissingEpisodes
        && missingCount >= airedCount * SeasonPackMissingRatio;
}
