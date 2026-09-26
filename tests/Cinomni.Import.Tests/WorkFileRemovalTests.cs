using Cinomni.Import.Application;
using Cinomni.Import.Files;
using Cinomni.Library.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Import.Tests;

/// <summary>
/// Deleting the library files of a work an administrator removed together with its files. It deletes in a
/// working library, so what matters most is what it refuses: anything outside the library root, and the
/// copies an upgrade set aside in the recycle folder, which were never the work's live files.
/// </summary>
public sealed class WorkFileRemovalTests
{
    private static readonly string LibraryRoot = Path.GetFullPath("/data/removal-library");
    private static readonly Guid WorkId = Guid.NewGuid();

    private static string Under(params string[] segments) => Path.Combine([LibraryRoot, .. segments]);

    [Fact]
    public async Task Every_live_file_of_the_work_is_deleted()
    {
        var film = Under("Film (2013)", "Film (2013).mkv");
        var library = new FakeLibrary(Asset(MediaAssetState.Removed, film));
        var files = new FakeImportFileSystem();

        await Build(library, files).RemoveAsync(WorkId);

        Assert.Equal([film], files.Deleted);
    }

    [Fact]
    public async Task A_path_outside_the_library_root_is_never_deleted()
    {
        var library = new FakeLibrary(
            Asset(MediaAssetState.Active, Path.GetFullPath("/etc/passwd")),
            Asset(MediaAssetState.Active, Under("..", "elsewhere", "file.mkv")));
        var files = new FakeImportFileSystem();

        await Build(library, files).RemoveAsync(WorkId);

        Assert.Empty(files.Deleted);
    }

    [Fact]
    public async Task An_upgraded_away_asset_is_left_alone()
    {
        // Its path is the one its replacement now occupies, and the copy it named is in the recycle folder.
        var library = new FakeLibrary(Asset(MediaAssetState.Upgraded, Under("Film (2013)", "Film (2013).mkv")));
        var files = new FakeImportFileSystem();

        await Build(library, files).RemoveAsync(WorkId);

        Assert.Empty(files.Deleted);
    }

    [Fact]
    public async Task A_work_with_no_assets_deletes_nothing()
    {
        var files = new FakeImportFileSystem();

        await Build(new FakeLibrary(), files).RemoveAsync(WorkId);

        Assert.Empty(files.Deleted);
    }

    private static WorkFileRemoval Build(FakeLibrary library, FakeImportFileSystem files) => new(
        library, files, new ImportOptions { LibraryRoot = LibraryRoot }, NullLogger<WorkFileRemoval>.Instance);

    private static MediaAssetDetail Asset(MediaAssetState state, string fullPath)
    {
        var id = new MediaAssetId(Guid.NewGuid());
        var version = new MediaVersionSummary(
            new MediaVersionId(Guid.NewGuid()), Path.GetFileName(fullPath), fullPath, 1, null, []);
        return new MediaAssetDetail(
            new MediaAssetSummary(id, WorkId, state, version.Id, DateTimeOffset.UnixEpoch), [version], []);
    }

    /// <summary>Answers the two reads the removal makes, and refuses every other.</summary>
    private sealed class FakeLibrary(params MediaAssetDetail[] assets) : ILibraryQuery
    {
        public Task<IReadOnlyList<MediaAssetSummary>> GetByWorkAsync(Guid workId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaAssetSummary>>(
                assets.Where(a => a.Asset.WorkId == workId).Select(a => a.Asset).ToList());

        public Task<MediaAssetDetail?> GetAsync(MediaAssetId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(assets.FirstOrDefault(a => a.Asset.Id == id));

        public Task<IReadOnlyList<MediaVersionPath>> ListActiveVersionPathsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MediaAssetSummary>> GetByUnitAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MediaAssetSummary>> GetByUnitsAsync(
            IReadOnlyList<Guid> unitIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReleaseQuality?> GetCurrentQualityAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MediaAssetSummary?> FindActiveByPathAsync(string fullPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindActiveWorksUnderAsync(string directory, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
