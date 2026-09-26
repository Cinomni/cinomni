using Cinomni.Monitoring.Application;
using Cinomni.Monitoring.Contracts;

namespace Cinomni.Monitoring.Tests;

/// <summary>
/// Pure guards on the two enums this slice widened. Both are persisted, so an accidental renumbering or
/// rename would silently reinterpret every existing <c>monitored_targets</c> row rather than fail a build.
/// </summary>
public sealed class MonitoringModeTests
{
    [Fact]
    public void Existing_modes_keep_their_persisted_integer_values()
    {
        // The two modes the movie slice shipped. Their values are the contract; the five hierarchical
        // modes were APPENDED above them and never renumbered.
        Assert.Equal(0, (int)MonitoringMode.None);
        Assert.Equal(1, (int)MonitoringMode.All);

        Assert.Equal(2, (int)MonitoringMode.Future);
        Assert.Equal(3, (int)MonitoringMode.Pilot);
        Assert.Equal(4, (int)MonitoringMode.FirstSeason);
        Assert.Equal(5, (int)MonitoringMode.LastSeason);
        Assert.Equal(6, (int)MonitoringMode.Existing);
    }

    [Fact]
    public void Existing_target_kinds_keep_their_persisted_names()
    {
        // TargetKind is stored as text (HasConversion<string>), so it is the NAMES that are the contract.
        Assert.Equal("Movie", TargetKind.Movie.ToString());
        Assert.Equal("Season", TargetKind.Season.ToString());
        Assert.Equal("Episode", TargetKind.Episode.ToString());
        Assert.Equal("Series", TargetKind.Series.ToString());

        // ...and the new member took the next free value rather than displacing one.
        Assert.Equal(4, (int)TargetKind.Series);
    }

    [Fact]
    public void Every_mode_has_a_cascade_rule()
    {
        // Walks the enum rather than listing it: appending a mode without teaching the policy what it
        // selects would otherwise monitor nothing at all, with no error anywhere.
        foreach (var mode in Enum.GetValues<MonitoringMode>())
        {
            var cascade = MonitoringModePolicy.CascadeFor(mode);
            Assert.True(Enum.IsDefined(cascade), $"Mode {mode} has no defined cascade.");
        }
    }

    [Fact]
    public void An_undefined_mode_has_no_cascade_rule()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitoringModePolicy.CascadeFor((MonitoringMode)99));
    }

    [Fact]
    public void The_air_instant_prefers_the_timezone_aware_value_and_falls_back_to_utc_midnight()
    {
        var stamp = new DateTimeOffset(2026, 7, 28, 21, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(stamp, MonitoringModePolicy.AirInstant(new DateOnly(2026, 7, 28), stamp));

        var midnight = MonitoringModePolicy.AirInstant(new DateOnly(2026, 7, 28), airDateTime: null);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero), midnight);

        Assert.Null(MonitoringModePolicy.AirInstant(airDate: null, airDateTime: null));
    }
}
