using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>
/// The move every caller uses to put a household's file somewhere else — the recycle bin, a repaired
/// name — against real disk. It must never land on top of another file: that other file is somebody's
/// other copy, and the one overwrite this adapter used to allow is how a relaunched import destroyed
/// the previous copy it had just filed in the bin.
/// </summary>
public sealed class LocalImportFileSystemMoveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cinomni-import-move-").FullName;
    private readonly LocalImportFileSystem _fileSystem = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_move_onto_an_existing_file_is_refused_and_both_files_survive()
    {
        var from = Path.Combine(_root, "incoming.mkv");
        var to = Path.Combine(_root, ".recycle", "kept.mkv");
        await File.WriteAllTextAsync(from, "the newer copy");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        await File.WriteAllTextAsync(to, "the copy the bin was keeping");

        await Assert.ThrowsAsync<IOException>(() => _fileSystem.MoveAsync(from, to));

        Assert.Equal("the copy the bin was keeping", await File.ReadAllTextAsync(to));
        Assert.Equal("the newer copy", await File.ReadAllTextAsync(from));
    }

    [Fact]
    public async Task A_landing_never_deletes_or_overwrites_a_different_file_already_there()
    {
        var source = Path.Combine(_root, "incoming.mkv");
        var target = Path.Combine(_root, "Film", "Film.mkv");
        await File.WriteAllTextAsync(source, "the new release, longer");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "somebody's film");

        await Assert.ThrowsAsync<IOException>(() => _fileSystem.HardlinkOrCopyAsync(source, target));

        Assert.Equal("somebody's film", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Two_files_are_the_same_content_only_when_every_byte_is()
    {
        var first = Path.Combine(_root, "a.mkv");
        var copy = Path.Combine(_root, "b.mkv");
        var sameStartOtherEnd = Path.Combine(_root, "c.mkv");
        var prefix = new string('x', 2 * 1024 * 1024);
        await File.WriteAllTextAsync(first, prefix + "tail-one");
        await File.WriteAllTextAsync(copy, prefix + "tail-one");
        await File.WriteAllTextAsync(sameStartOtherEnd, prefix + "tail-two");

        Assert.True(await _fileSystem.HasSameContentAsync(first, copy));
        Assert.False(await _fileSystem.HasSameContentAsync(first, sameStartOtherEnd));
    }

    [Fact]
    public async Task A_move_to_a_free_place_lands_and_repeating_it_is_a_no_op()
    {
        var from = Path.Combine(_root, "film.mkv");
        var to = Path.Combine(_root, "Film", "film.mkv");
        await File.WriteAllTextAsync(from, "film");

        await _fileSystem.MoveAsync(from, to);
        await _fileSystem.MoveAsync(from, to);

        Assert.False(File.Exists(from));
        Assert.Equal("film", await File.ReadAllTextAsync(to));
    }
}
