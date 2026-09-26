using Cinomni.Catalog.Contracts;
using Cinomni.Monitoring.Contracts;

namespace Cinomni.Monitoring.Application;

/// <summary>What a <see cref="MonitoringMode"/> selects out of a series' episodes.</summary>
public enum EpisodeCascade
{
    /// <summary>Nothing is watched.</summary>
    Nothing = 0,

    /// <summary>Every episode, specials included.</summary>
    Every = 1,

    /// <summary>Only episodes whose air instant is still in the future.</summary>
    Unaired = 2,

    /// <summary>Only episodes that have already aired (an unknown air date counts as aired).</summary>
    Aired = 3,

    /// <summary>Only episode 1 of the first real season.</summary>
    Pilot = 4,

    /// <summary>Every episode of the lowest-numbered real season.</summary>
    FirstSeason = 5,

    /// <summary>Every episode of the highest-numbered real season.</summary>
    LastSeason = 6,
}

/// <summary>
/// The lowest and highest <em>real</em> season numbers of a series. Season 0 holds specials and is
/// deliberately excluded: "first season" means S01, not the specials bucket, and a series whose only
/// season is 0 has no real season at all.
/// </summary>
public sealed record SeriesShape(int? FirstSeasonNumber, int? LastSeasonNumber)
{
    /// <summary>Season number reserved for specials by every provider this platform reads.</summary>
    public const int SpecialsSeasonNumber = 0;

    public static SeriesShape Empty { get; } = new(null, null);

    /// <summary>Derives the shape from the season numbers a series actually has.</summary>
    public static SeriesShape From(IEnumerable<int> seasonNumbers)
    {
        var real = seasonNumbers.Where(n => n > SpecialsSeasonNumber).ToList();
        return real.Count == 0 ? Empty : new SeriesShape(real.Min(), real.Max());
    }
}

/// <summary>
/// The single place that answers "does this mode watch this episode?".
/// <para>
/// <see cref="CascadeFor"/> is an exhaustive switch over <see cref="MonitoringMode"/> on purpose: the
/// enum widened from two members to seven in this slice, and a mode with no cascade rule would silently
/// monitor nothing rather than fail. <c>MonitoringModeTests.Every_mode_has_a_cascade_rule</c> walks
/// <see cref="Enum.GetValues{TEnum}()"/> so a future append is caught by a red test, not by a user
/// wondering why their series never downloads.
/// </para>
/// </summary>
public static class MonitoringModePolicy
{
    /// <summary>The cascade rule of a mode. Throws for a value outside the enum (never persisted).</summary>
    public static EpisodeCascade CascadeFor(MonitoringMode mode) => mode switch
    {
        MonitoringMode.None => EpisodeCascade.Nothing,
        MonitoringMode.All => EpisodeCascade.Every,
        MonitoringMode.Future => EpisodeCascade.Unaired,
        MonitoringMode.Pilot => EpisodeCascade.Pilot,
        MonitoringMode.FirstSeason => EpisodeCascade.FirstSeason,
        MonitoringMode.LastSeason => EpisodeCascade.LastSeason,
        MonitoringMode.Existing => EpisodeCascade.Aired,
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode), mode, "No cascade rule is defined for this monitoring mode."),
    };

    /// <summary>Whether <paramref name="mode"/> watches <paramref name="episode"/> of a series shaped like <paramref name="shape"/>.</summary>
    public static bool ShouldMonitor(
        MonitoringMode mode,
        EpisodeSummary episode,
        SeriesShape shape,
        DateTimeOffset now) =>
        ShouldMonitor(CascadeFor(mode), episode, shape, now);

    /// <summary>Whether <paramref name="cascade"/> watches <paramref name="episode"/>.</summary>
    public static bool ShouldMonitor(
        EpisodeCascade cascade,
        EpisodeSummary episode,
        SeriesShape shape,
        DateTimeOffset now)
    {
        var airsAt = AirInstant(episode.AirDate, episode.AirDateTime);

        return cascade switch
        {
            EpisodeCascade.Nothing => false,
            EpisodeCascade.Every => true,
            EpisodeCascade.Unaired => airsAt is { } instant && instant > now,
            // An unknown air date counts as aired: a back-catalogue provider commonly omits dates, and
            // Unaired/Aired must partition the episodes so no episode falls through both modes.
            EpisodeCascade.Aired => airsAt is not { } aired || aired <= now,
            EpisodeCascade.Pilot => episode.SeasonNumber == shape.FirstSeasonNumber && episode.Number == 1,
            EpisodeCascade.FirstSeason => episode.SeasonNumber == shape.FirstSeasonNumber,
            EpisodeCascade.LastSeason => episode.SeasonNumber == shape.LastSeasonNumber,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cascade), cascade, "No selection rule is defined for this cascade."),
        };
    }

    /// <summary>
    /// The instant an episode airs: prefer the provider's timezone-aware
    /// <paramref name="airDateTime"/>, otherwise read the published date at UTC midnight. Everything
    /// stored and compared is UTC — this module never calls <c>DateTime.Now</c>.
    /// </summary>
    public static DateTimeOffset? AirInstant(DateOnly? airDate, DateTimeOffset? airDateTime)
    {
        if (airDateTime is { } instant)
        {
            return instant;
        }

        return airDate is { } date
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            : null;
    }
}
