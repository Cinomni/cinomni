using Cinomni.Import.Application;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;
using Cinomni.Kernel.Messaging;
using Cinomni.Library.Contracts;
using Cinomni.Operations.Messaging;
using Cinomni.Operations.Transactions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinomni.Import.Tests;

/// <summary>
/// The repair pass over names an earlier build wrote. What is tested here is mostly what it refuses to
/// do: it renames files in a working library, so overwriting, guessing, or touching anything outside
/// the library root would each cost a household its content.
/// </summary>
public sealed class LibraryPathRepairTests
{
    private static readonly string LibraryRoot = Path.GetFullPath("/data/repair-library");

    private static string Under(params string[] segments) => Path.Combine([LibraryRoot, .. segments]);

    /// <summary>Answers the one read the pass makes, and refuses every other.</summary>
    private sealed class FakeLibraryPaths(params MediaVersionPath[] paths) : ILibraryQuery
    {
        public Task<IReadOnlyList<MediaVersionPath>> ListActiveVersionPathsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaVersionPath>>(paths);

        public Task<IReadOnlyList<MediaAssetSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MediaAssetDetail?> GetAsync(MediaAssetId id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MediaAssetSummary>> GetByWorkAsync(Guid workId, CancellationToken cancellationToken = default) =>
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

    /// <summary>Records what was published instead of writing an outbox row.</summary>
    private sealed class RecordingEventBus : IEventBus
    {
        public List<IDomainEvent> Published { get; } = [];

        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Published.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>Runs the work directly: there is no database in these tests, only the ordering to keep.</summary>
    private sealed class DirectUnitOfWork : IUnitOfWork
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
            work(cancellationToken);
    }

    private static (LibraryPathRepair Repair, FakeImportFileSystem Files, RecordingEventBus Events) Build(
        params MediaVersionPath[] stored)
    {
        var options = new ImportOptions { LibraryRoot = LibraryRoot };
        var files = new FakeImportFileSystem();
        var events = new RecordingEventBus();
        var repair = new LibraryPathRepair(
            new FakeLibraryPaths(stored),
            files,
            new LibraryPathSanitizer(options),
            events,
            new DirectUnitOfWork(),
            options,
            NullLogger<LibraryPathRepair>.Instance);

        return (repair, files, events);
    }

    [Fact]
    public async Task A_file_with_a_forbidden_character_is_moved_and_announced()
    {
        var assetId = Guid.NewGuid();
        var from = Under("Alien: Cut", "Alien: Cut.mkv");
        var (repair, files, events) = Build(new MediaVersionPath(assetId, Guid.NewGuid(), from));
        files.SeedContent(Path.GetDirectoryName(from)!, new ImportFileEntry(from, 1000));

        var report = await repair.RepairAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(PathRepairOutcome.Repaired, entry.Outcome);
        Assert.Equal(Under("Alien_ Cut", "Alien_ Cut.mkv"), entry.ToPath);
        Assert.Contains(files.Moved, m => m.To == entry.ToPath);

        // The announcement is what lets Library and Subtitles correct their own rows.
        var relocated = Assert.IsType<MediaFileRelocated>(Assert.Single(events.Published));
        Assert.Equal(assetId, relocated.AssetId);
        Assert.Equal(from, relocated.FromPath);
    }

    [Fact]
    public async Task Sidecars_travel_with_the_video()
    {
        // A subtitle is named from the video's stem and lives beside it. Leaving it behind would strand
        // a track the household asked for under a name nothing would ever look up again.
        var from = Under("Movie: Cut", "Movie: Cut.mkv");
        var directory = Path.GetDirectoryName(from)!;
        var subtitle = Path.Combine(directory, "Movie: Cut.es.forced.srt");
        var (repair, files, _) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(directory, new ImportFileEntry(from, 1000), new ImportFileEntry(subtitle, 20));

        var report = await repair.RepairAsync();

        Assert.Equal(1, Assert.Single(report.Entries).Sidecars);
        Assert.Contains(files.Moved, m => m.To == Path.Combine(Under("Movie_ Cut"), "Movie_ Cut.es.forced.srt"));
    }

    [Fact]
    public async Task Only_what_continues_the_video_name_after_a_dot_and_is_not_a_video_travels_with_it()
    {
        // Starting with the same letters is not being a sidecar: a second film, an extended cut and a
        // loose note all used to be moved and renamed as if they were this video's subtitles.
        var from = Under("Movie: Cut", "Movie: Cut.mkv");
        var directory = Path.GetDirectoryName(from)!;
        string[] strangers =
        [
            Path.Combine(directory, "Movie: Cut 2.mkv"),
            Path.Combine(directory, "Movie: Cut.extended.mkv"),
            Path.Combine(directory, "Movie: Cut-notes.txt"),
        ];
        var subtitle = Path.Combine(directory, "Movie: Cut.es.srt");
        var (repair, files, _) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(
            directory,
            [new ImportFileEntry(from, 1000), new ImportFileEntry(subtitle, 20), .. strangers.Select(s => new ImportFileEntry(s, 500))]);

        var report = await repair.RepairAsync();

        Assert.Equal(1, Assert.Single(report.Entries).Sidecars);
        Assert.Contains(files.Moved, m => m.From == subtitle);
        Assert.All(strangers, stranger => Assert.DoesNotContain(files.Moved, m => m.From == stranger));
        Assert.All(strangers, stranger => Assert.True(files.FileExists(stranger)));
    }

    [Fact]
    public async Task A_sidecar_whose_new_name_is_taken_blocks_the_repair_before_anything_moves()
    {
        // Subtitles renames its rows by the video's new name when the move is announced: a sidecar left
        // behind would have its row pointed at the file that took its name, and nothing would correct it.
        var from = Under("Movie: Cut", "Movie: Cut.mkv");
        var directory = Path.GetDirectoryName(from)!;
        var subtitle = Path.Combine(directory, "Movie: Cut.es.srt");
        var taken = Path.Combine(Under("Movie_ Cut"), "Movie_ Cut.es.srt");
        var (repair, files, events) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(directory, new ImportFileEntry(from, 1000), new ImportFileEntry(subtitle, 20));
        files.SeedFile(taken, 7);

        var report = await repair.RepairAsync();

        Assert.Equal(PathRepairOutcome.Blocked, Assert.Single(report.Entries).Outcome);
        Assert.Empty(files.Moved);
        Assert.Empty(events.Published);
        Assert.Equal(7, files.GetSize(taken));
    }

    [Fact]
    public async Task An_occupied_sanitised_path_blocks_the_repair_instead_of_overwriting()
    {
        // Two names differing only by a forbidden character collapse onto one. Picking a winner would
        // destroy a file to tidy a name.
        var from = Under("Movie", "Film: Cut.mkv");
        var occupied = Under("Movie", "Film_ Cut.mkv");
        var (repair, files, events) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(Path.GetDirectoryName(from)!, new ImportFileEntry(from, 1000), new ImportFileEntry(occupied, 900));

        var report = await repair.RepairAsync();

        Assert.Equal(PathRepairOutcome.Blocked, Assert.Single(report.Entries).Outcome);
        Assert.Empty(files.Moved);
        Assert.Empty(events.Published);
    }

    [Fact]
    public async Task A_file_already_moved_by_an_interrupted_pass_is_announced_without_touching_disk()
    {
        // The crash window: the move landed, the event never got written. The library still names the
        // old path, so the next pass has to close it after the fact rather than start over.
        var from = Under("Movie", "Film: Cut.mkv");
        var to = Under("Movie", "Film_ Cut.mkv");
        var (repair, files, events) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(Path.GetDirectoryName(from)!, new ImportFileEntry(to, 1000));

        var report = await repair.RepairAsync();

        Assert.Equal(PathRepairOutcome.Announced, Assert.Single(report.Entries).Outcome);
        Assert.Empty(files.Moved);
        Assert.Single(events.Published);
    }

    [Fact]
    public async Task A_second_pass_never_moves_the_same_file_twice()
    {
        var from = Under("Alien: Cut", "Alien: Cut.mkv");
        var (repair, files, events) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(Path.GetDirectoryName(from)!, new ImportFileEntry(from, 1000));

        await repair.RepairAsync();
        var moves = files.Moved.Count;
        var announcements = events.Published.Count;

        // The stored path still names the old file — the rows only catch up when the command runs — so
        // the second pass finds the already-moved state and re-announces rather than moving again.
        var second = await repair.RepairAsync();

        Assert.Equal(PathRepairOutcome.Announced, Assert.Single(second.Entries).Outcome);
        Assert.Equal(moves, files.Moved.Count);
        Assert.Equal(announcements + 1, events.Published.Count);
    }

    [Fact]
    public async Task A_clean_library_reports_nothing_to_do()
    {
        var clean = Under("The Wire (2002)", "Season 01", "The Wire - S01E01.mkv");
        var (repair, files, events) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), clean));
        files.SeedContent(Path.GetDirectoryName(clean)!, new ImportFileEntry(clean, 1000));

        Assert.Empty((await repair.RepairAsync()).Entries);
        Assert.Empty(files.Moved);
        Assert.Empty(events.Published);
    }

    [Fact]
    public async Task An_inaccessible_library_root_repairs_nothing()
    {
        // Every path would read as absent on an unmounted volume, and the pass would report a clean
        // library while the files sat on the mount that is missing.
        var from = Under("Movie", "Film: Cut.mkv");
        var (repair, files, _) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(Path.GetDirectoryName(from)!, new ImportFileEntry(from, 1000));
        files.RootIsAccessible = false;

        Assert.Empty((await repair.RepairAsync()).Entries);
        Assert.Empty(files.Moved);
    }

    [Fact]
    public async Task A_stored_path_outside_the_library_is_never_touched()
    {
        var outside = OperatingSystem.IsWindows() ? @"C:\elsewhere\Film: Cut.mkv" : "/elsewhere/Film: Cut.mkv";
        var (repair, files, events) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), outside));
        files.SeedContent(Path.GetDirectoryName(outside)!, new ImportFileEntry(outside, 1000));

        Assert.Empty((await repair.RepairAsync()).Entries);
        Assert.Empty(files.Moved);
        Assert.Empty(events.Published);
    }

    [Fact]
    public async Task The_preview_moves_nothing()
    {
        var from = Under("Movie", "Film: Cut.mkv");
        var (repair, files, events) = Build(new MediaVersionPath(Guid.NewGuid(), Guid.NewGuid(), from));
        files.SeedContent(Path.GetDirectoryName(from)!, new ImportFileEntry(from, 1000));

        var report = await repair.PreviewAsync();

        Assert.Equal(PathRepairOutcome.Repairable, Assert.Single(report.Entries).Outcome);
        Assert.Empty(files.Moved);
        Assert.Empty(events.Published);
    }
}
