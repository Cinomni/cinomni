using Cinomni.Metadata.Application;
using Cinomni.Metadata.Contracts;
using Cinomni.Metadata.Persistence;
using Cinomni.Metadata.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cinomni.Metadata.Tests;

/// <summary>
/// Integration tests for the operator artwork override once artwork can be scoped to a season or an
/// episode. A season poster and the series poster are both <c>Poster</c> rows of the same snapshot, so an
/// override that clears "every row of this kind" and re-points the snapshot's urls would let the first
/// season poster an operator picks silently replace the series poster shown everywhere.
/// </summary>
public sealed class ScopedArtworkSelectionTests : IAsyncLifetime
{
    private const string SeriesPosterUrl = "https://image.example/series-poster.jpg";
    private const string SeriesPosterAltUrl = "https://image.example/series-poster-alt.jpg";
    private const string SeasonPosterUrl = "https://image.example/season-1.jpg";
    private const string SeasonPosterAltUrl = "https://image.example/season-1-alt.jpg";
    private const string SeasonTwoPosterUrl = "https://image.example/season-2.jpg";
    private const string StillUrl = "https://image.example/s01e01.jpg";

    private ServiceProvider _host = null!;

    public async Task InitializeAsync() =>
        _host = await MetadataTestHost.CreateAsync("cinomni_test_metadata_artwork_scope", sources: []);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Selecting_a_season_poster_does_not_deselect_the_series_poster()
    {
        var snapshotId = await SeedAsync();
        var seasonAlt = await ArtworkIdAsync(snapshotId, SeasonPosterAltUrl);

        Assert.True((await SelectAsync(snapshotId, seasonAlt)).IsSuccess);

        var artwork = await ArtworkAsync(snapshotId);
        Assert.True(artwork.Single(a => a.Url == SeriesPosterUrl).IsSelected);
        Assert.True(artwork.Single(a => a.Url == SeasonPosterAltUrl).IsSelected);
        Assert.False(artwork.Single(a => a.Url == SeasonPosterUrl).IsSelected);
        // Another season's selection is a different scope again and is untouched.
        Assert.True(artwork.Single(a => a.Url == SeasonTwoPosterUrl).IsSelected);
    }

    [Fact]
    public async Task Selecting_a_season_poster_does_not_overwrite_the_snapshot_urls()
    {
        var snapshotId = await SeedAsync();
        var seasonAlt = await ArtworkIdAsync(snapshotId, SeasonPosterAltUrl);

        Assert.True((await SelectAsync(snapshotId, seasonAlt)).IsSuccess);

        var snapshot = await SnapshotAsync(snapshotId);
        Assert.Equal(SeriesPosterUrl, snapshot.PosterUrl);
    }

    [Fact]
    public async Task Selecting_a_series_poster_still_repoints_the_snapshot()
    {
        // The movie regression: for a movie every candidate is series-level, so this is exactly the
        // behaviour that ships today and it must be bit-for-bit unchanged.
        var snapshotId = await SeedAsync();
        var seriesAlt = await ArtworkIdAsync(snapshotId, SeriesPosterAltUrl);

        Assert.True((await SelectAsync(snapshotId, seriesAlt)).IsSuccess);

        var snapshot = await SnapshotAsync(snapshotId);
        Assert.Equal(SeriesPosterAltUrl, snapshot.PosterUrl);

        var artwork = await ArtworkAsync(snapshotId);
        Assert.True(artwork.Single(a => a.Url == SeriesPosterAltUrl).IsSelected);
        Assert.False(artwork.Single(a => a.Url == SeriesPosterUrl).IsSelected);
        // The season scope was not disturbed by a series-level pick either.
        Assert.True(artwork.Single(a => a.Url == SeasonPosterUrl).IsSelected);
    }

    [Fact]
    public async Task Selecting_an_episode_still_leaves_every_other_scope_alone()
    {
        var snapshotId = await SeedAsync();
        var still = await ArtworkIdAsync(snapshotId, StillUrl);

        Assert.True((await SelectAsync(snapshotId, still)).IsSuccess);

        var snapshot = await SnapshotAsync(snapshotId);
        Assert.Equal(SeriesPosterUrl, snapshot.PosterUrl);
        Assert.Equal("https://image.example/series-backdrop.jpg", snapshot.BackdropUrl);
    }

    [Fact]
    public async Task Selecting_an_unknown_candidate_fails()
    {
        var snapshotId = await SeedAsync();

        var result = await SelectAsync(snapshotId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("metadata.artwork_not_found", result.Error.Code);
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<Guid> SeedAsync()
    {
        await using var scope = _host.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        var snapshot = MetadataSnapshotRecord.Create(
            Guid.NewGuid(), "tvdb", MetadataMediaKind.Series, "121361", "The Frontier", null, 2002, null, 60, "en",
            SeriesPosterUrl, "https://image.example/series-backdrop.jpg", "{}", DateTimeOffset.UtcNow,
            new SeriesSnapshotDetails(SeriesStatus.Continuing, SeasonOrder: SeasonOrders.Official));

        // (kind, season, episode) is the selection scope: one selected candidate in each.
        Add(snapshot, Poster(SeriesPosterUrl), isSelected: true, ordinal: 0);
        Add(snapshot, Poster(SeriesPosterAltUrl), isSelected: false, ordinal: 1);
        Add(snapshot, new ProviderArtwork(ArtworkKind.Backdrop, "https://image.example/series-backdrop.jpg", null, 3840, 2160, null, null), true, 2);
        Add(snapshot, Poster(SeasonPosterUrl), isSelected: true, ordinal: 3, seasonNumber: 1);
        Add(snapshot, Poster(SeasonPosterAltUrl), isSelected: false, ordinal: 4, seasonNumber: 1);
        Add(snapshot, Poster(SeasonTwoPosterUrl), isSelected: true, ordinal: 5, seasonNumber: 2);
        Add(snapshot, new ProviderArtwork(ArtworkKind.Still, StillUrl, null, 1920, 1080, null, null), false, 6, 1, 1);

        dbContext.Snapshots.Add(snapshot);
        await dbContext.SaveChangesAsync();
        return snapshot.Id;
    }

    private static void Add(
        MetadataSnapshotRecord snapshot,
        ProviderArtwork artwork,
        bool isSelected,
        int ordinal,
        int? seasonNumber = null,
        int? episodeNumber = null) =>
        snapshot.Artwork.Add(MetadataArtworkRecord.Create(snapshot.Id, artwork, isSelected, ordinal, seasonNumber, episodeNumber));

    private static ProviderArtwork Poster(string url) =>
        new(ArtworkKind.Poster, url, "en", 2000, 3000, VoteAverage: 8.0, VoteCount: 10);

    private async Task<Guid> ArtworkIdAsync(Guid snapshotId, string url)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MetadataDbContext>()
            .Artwork.AsNoTracking()
            .Where(a => a.SnapshotId == snapshotId && a.Url == url)
            .Select(a => a.Id)
            .SingleAsync();
    }

    private async Task<Cinomni.Kernel.Results.Result> SelectAsync(Guid snapshotId, Guid artworkId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMetadataArtwork>()
            .SelectAsync(new MetadataSnapshotId(snapshotId), artworkId);
    }

    private async Task<List<MetadataArtworkRecord>> ArtworkAsync(Guid snapshotId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MetadataDbContext>()
            .Artwork.AsNoTracking().Where(a => a.SnapshotId == snapshotId).ToListAsync();
    }

    private async Task<MetadataSnapshotRecord> SnapshotAsync(Guid snapshotId)
    {
        await using var scope = _host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MetadataDbContext>()
            .Snapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId);
    }
}
