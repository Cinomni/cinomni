using Cinomni.Catalog.Contracts;

namespace Cinomni.Catalog.Tests;

/// <summary>
/// Pure guards on the persisted shape of the Catalog contracts.
/// <para>
/// <c>works.status</c>, <c>works.kind</c> and <c>external_identifiers.provider</c> are stored as TEXT via
/// <c>HasConversion&lt;string&gt;()</c>, so their enums may only ever be APPENDED to: renaming a member
/// corrupts every existing row and nothing in the build catches it. The integration events are stored as
/// jsonb in <c>operations.outbox</c> under an idempotency key that is consumed forever, so the key shape
/// is part of the contract too.
/// </para>
/// </summary>
public sealed class CatalogContractsTests
{
    [Fact]
    public void Work_status_values_keep_their_persisted_names()
    {
        Assert.Equal("Unknown", WorkStatus.Unknown.ToString());
        Assert.Equal("Announced", WorkStatus.Announced.ToString());
        Assert.Equal("Released", WorkStatus.Released.ToString());

        // A stored row round-trips by name, which is what the text column actually holds.
        Assert.Equal(WorkStatus.Unknown, Enum.Parse<WorkStatus>("Unknown"));
        Assert.Equal(WorkStatus.Announced, Enum.Parse<WorkStatus>("Announced"));
        Assert.Equal(WorkStatus.Released, Enum.Parse<WorkStatus>("Released"));

        // The shipped members are never renumbered either.
        Assert.Equal(0, (int)WorkStatus.Unknown);
        Assert.Equal(1, (int)WorkStatus.Announced);
        Assert.Equal(2, (int)WorkStatus.Released);

        // The series lifecycle states are appended after them, never inserted among them.
        Assert.Equal("Continuing", WorkStatus.Continuing.ToString());
        Assert.Equal("Ended", WorkStatus.Ended.ToString());
        Assert.Equal(3, (int)WorkStatus.Continuing);
        Assert.Equal(4, (int)WorkStatus.Ended);
    }

    [Fact]
    public void Metadata_provider_values_keep_their_persisted_names()
    {
        Assert.Equal("Tmdb", MetadataProvider.Tmdb.ToString());
        Assert.Equal("Imdb", MetadataProvider.Imdb.ToString());
        Assert.Equal("Tvdb", MetadataProvider.Tvdb.ToString());

        Assert.Equal(1, (int)MetadataProvider.Tmdb);
        Assert.Equal(2, (int)MetadataProvider.Imdb);
        Assert.Equal(3, (int)MetadataProvider.Tvdb);

        // TVMaze is appended: a series found through it has no other external id to dedupe on, and
        // rule-16 identity reuse would otherwise mint a duplicate work on every refresh.
        Assert.Equal("TvMaze", MetadataProvider.TvMaze.ToString());
        Assert.Equal(4, (int)MetadataProvider.TvMaze);
    }

    [Fact]
    public void Work_kind_values_keep_their_persisted_names()
    {
        Assert.Equal("Movie", WorkKind.Movie.ToString());
        Assert.Equal("Series", WorkKind.Series.ToString());
        Assert.Equal(1, (int)WorkKind.Movie);
        Assert.Equal(2, (int)WorkKind.Series);
    }

    [Fact]
    public void Series_structure_changed_is_keyed_by_work_and_snapshot()
    {
        var workId = Guid.NewGuid();
        var first = new SeriesStructureChanged(workId, Guid.NewGuid(), "tvdb", 1, 10, [], []);
        var second = new SeriesStructureChanged(workId, Guid.NewGuid(), "tvdb", 2, 20, [], []);

        Assert.Equal($"series-structure:{workId}:{first.SnapshotId}", first.IdempotencyKey);

        // Idempotency keys are consumed forever. A series legitimately grows a season with every later
        // snapshot, so keying on the work alone would silently swallow every one of them.
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void Episode_available_is_keyed_by_the_episode_not_the_asset()
    {
        var assetId = Guid.NewGuid();
        var firstEpisode = Guid.NewGuid();
        var secondEpisode = Guid.NewGuid();

        var first = new EpisodeAvailable(Guid.NewGuid(), Guid.NewGuid(), firstEpisode, 1, 1, assetId, Guid.NewGuid());
        var second = new EpisodeAvailable(Guid.NewGuid(), Guid.NewGuid(), secondEpisode, 1, 2, assetId, Guid.NewGuid());

        Assert.Equal($"episode-available:{firstEpisode}", first.IdempotencyKey);

        // One season pack is one asset covering N episodes: keying on the asset would collapse the
        // whole fan-out into a single fact instead of N that deduplicate independently.
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void A_movie_summary_reports_no_episode_rollups()
    {
        var summary = new WorkSummary(WorkId.New(), WorkKind.Movie, "The Matrix", 1999, WorkStatus.Released, true, []);

        Assert.Equal(0, summary.EpisodeCount);
        Assert.Equal(0, summary.AvailableEpisodeCount);
    }
}
