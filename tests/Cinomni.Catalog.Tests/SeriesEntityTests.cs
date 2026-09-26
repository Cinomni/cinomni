using Cinomni.Catalog.Persistence;
using Cinomni.Kernel.Identifiers;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Pure tests for the Season/Episode entity behaviour: availability is announced exactly once, and a
/// metadata refresh only ever fills in what the snapshot actually supplies.
/// </summary>
public sealed class SeriesEntityTests
{
    [Fact]
    public void Marking_an_episode_available_twice_reports_the_transition_once()
    {
        var episode = NewEpisode();

        Assert.True(episode.MarkAvailable());
        Assert.True(episode.HasAsset);

        // The second call is what makes a redelivered season-pack import safe: the caller skips the
        // roll-up and the event instead of counting the same episode twice.
        Assert.False(episode.MarkAvailable());
        Assert.True(episode.HasAsset);
    }

    [Fact]
    public void An_episode_snapshot_only_overwrites_the_fields_it_supplies()
    {
        var episode = NewEpisode();
        var snapshotId = Uuid7.New();
        episode.ApplyMetadata("Pilot", 1, new DateOnly(2002, 6, 2), null, 62, "https://img/still.jpg", snapshotId);

        // A sparser later snapshot must not blank what a richer earlier one filled in.
        var secondSnapshotId = Uuid7.New();
        episode.ApplyMetadata(null, null, null, null, null, null, secondSnapshotId);

        Assert.Equal("Pilot", episode.Title);
        Assert.Equal(1, episode.AbsoluteNumber);
        Assert.Equal(new DateOnly(2002, 6, 2), episode.AirDate);
        Assert.Equal(62, episode.RuntimeMinutes);
        Assert.Equal("https://img/still.jpg", episode.StillUrl);
        Assert.Equal(secondSnapshotId, episode.MetadataSnapshotId);
    }

    [Fact]
    public void An_episode_snapshot_never_makes_a_downloaded_episode_look_missing()
    {
        var episode = NewEpisode();
        episode.MarkAvailable();

        episode.ApplyMetadata("Renamed", null, null, null, null, null, Uuid7.New());

        Assert.True(episode.HasAsset);
    }

    [Fact]
    public void An_air_time_is_normalised_to_utc()
    {
        var episode = NewEpisode();
        var local = new DateTimeOffset(2002, 6, 2, 21, 0, 0, TimeSpan.FromHours(-4));

        episode.ApplyMetadata(null, null, null, local, null, null, Uuid7.New());

        Assert.Equal(TimeSpan.Zero, episode.AirDateTime!.Value.Offset);
        Assert.Equal(local.UtcDateTime, episode.AirDateTime!.Value.UtcDateTime);
    }

    [Fact]
    public void An_oversized_episode_title_is_truncated_before_persist()
    {
        var episode = NewEpisode();

        episode.ApplyMetadata(new string('x', Episode.TitleMaxLength + 100), null, null, null, null, null, Uuid7.New());

        Assert.Equal(Episode.TitleMaxLength, episode.Title!.Length);
    }

    [Fact]
    public void A_season_snapshot_only_overwrites_the_fields_it_supplies()
    {
        var season = new Season
        {
            Id = Uuid7.New(),
            WorkId = Uuid7.New(),
            Number = 1,
            AddedAt = DateTimeOffset.UtcNow,
        };

        season.ApplyMetadata("Season 1", new DateOnly(2002, 6, 2), 13, "https://img/poster.jpg", Uuid7.New());
        season.ApplyMetadata(null, null, null, null, Uuid7.New());

        Assert.Equal("Season 1", season.Title);
        Assert.Equal(new DateOnly(2002, 6, 2), season.AirDate);
        Assert.Equal(13, season.ExpectedEpisodeCount);
        Assert.Equal("https://img/poster.jpg", season.PosterUrl);
    }

    private static Episode NewEpisode() => new()
    {
        Id = Uuid7.New(),
        SeasonId = Uuid7.New(),
        WorkId = Uuid7.New(),
        SeasonNumber = 1,
        Number = 1,
        AddedAt = DateTimeOffset.UtcNow,
    };
}
