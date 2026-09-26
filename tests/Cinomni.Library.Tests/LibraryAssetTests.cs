using Cinomni.Library.Contracts;
using Cinomni.Library.Persistence;

namespace Cinomni.Library.Tests;

/// <summary>Pure unit tests for the MediaAsset aggregate, MediaVersion mapping and the quality value-object.</summary>
public sealed class LibraryAssetTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<MediaStreamInput> Streams =
    [
        new MediaStreamInput(0, MediaStreamType.Video, "h264", null, null, 1920, 1080, null, null, true, false),
        new MediaStreamInput(1, MediaStreamType.Audio, "aac", "eng", 6, null, null, null, null, true, false),
    ];

    [Fact]
    public void Create_starts_active()
    {
        var asset = MediaAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        Assert.Equal(MediaAssetState.Active, asset.State);
        Assert.Null(asset.PrimaryVersionId);
    }

    [Fact]
    public void AddVersion_sets_the_first_as_primary()
    {
        var asset = MediaAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Now);
        var version = MediaVersion.Create(asset.Id, "movie.mkv", "/lib/movie/movie.mkv", 1000, MediaQuality.Unknown, null, Streams);

        asset.AddVersion(version);

        Assert.Equal(version.Id, asset.PrimaryVersionId);
        Assert.Single(asset.Versions);
    }

    [Fact]
    public void LinkTarget_is_idempotent_and_skips_empty()
    {
        var asset = MediaAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Now);
        var targetId = Guid.NewGuid();

        asset.LinkTarget(targetId);
        asset.LinkTarget(targetId); // duplicate → no-op
        asset.LinkTarget(Guid.Empty); // empty → skipped

        Assert.Single(asset.TargetLinks);
        Assert.Equal(targetId, asset.TargetLinks[0].TargetId);
    }

    [Fact]
    public void LinkUnit_is_idempotent_and_skips_empty_guids()
    {
        var asset = MediaAsset.Create(Guid.NewGuid(), Guid.NewGuid(), Now);
        var unitId = Guid.NewGuid();

        Assert.NotNull(asset.LinkUnit(unitId));
        Assert.Null(asset.LinkUnit(unitId));    // duplicate → no link added
        Assert.Null(asset.LinkUnit(Guid.Empty)); // empty → skipped

        Assert.Single(asset.UnitLinks);
        Assert.Equal(unitId, asset.UnitLinks[0].UnitId);
    }

    [Fact]
    public void MediaVersion_maps_streams_relationally()
    {
        var version = MediaVersion.Create(Guid.NewGuid(), "movie.mkv", "/lib/movie/movie.mkv", 1000, MediaQuality.Unknown, null, Streams);

        Assert.Equal(2, version.Streams.Count);
        Assert.Equal(MediaStreamType.Video, version.Streams[0].Type);
        Assert.Equal("h264", version.Streams[0].Codec);
        Assert.Equal(1080, version.Streams[0].Height);
        Assert.Equal("eng", version.Streams[1].Language);
        Assert.False(version.Streams[0].IsExternal);
    }

    [Theory]
    [InlineData(2160, "2160p")]
    [InlineData(1080, "1080p")]
    [InlineData(720, "720p")]
    [InlineData(480, "480p")]
    public void MediaQuality_derives_resolution_from_height(int height, string expected)
    {
        Assert.Equal(expected, MediaQuality.FromHeight(height).Resolution);
    }

    [Fact]
    public void MediaQuality_is_unknown_without_height()
    {
        Assert.Null(MediaQuality.FromHeight(null).Resolution);
        Assert.Null(MediaQuality.FromHeight(0).Resolution);
    }

    [Fact]
    public void MediaQuality_json_round_trips()
    {
        var quality = new MediaQuality("BluRay", "1080p", "Remux", 1);

        var restored = MediaQuality.FromJson(quality.ToJson());

        Assert.Equal(quality, restored);
    }
}
