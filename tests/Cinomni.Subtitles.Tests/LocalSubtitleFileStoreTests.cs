using System.Text;
using Cinomni.Subtitles.Files;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// The production file store against a real directory, for the guarantees a fake cannot show: a
/// synchronized copy never lands on a file that is already there, and removing one never reaches a file
/// this module did not write or one in another directory.
/// </summary>
public sealed class LocalSubtitleFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cinomni-subtitle-store", Guid.NewGuid().ToString("N"));
    private readonly LocalSubtitleFileStore _store = new();

    public LocalSubtitleFileStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_copy_lands_beside_the_original_and_leaves_a_households_own_synced_file_alone()
    {
        // Arrange — the household already keeps a ".synced" file of its own, and a previous copy is playing.
        var original = Write("Movie.en.srt", "original");
        var theirs = Write("Movie.en.synced.srt", "theirs");
        var playing = await _store.WriteSyncedAsync(original, Encoding.UTF8.GetBytes("first pass"));

        // Act
        var copy = await _store.WriteSyncedAsync(original, Encoding.UTF8.GetBytes("second pass"));

        // Assert
        Assert.Equal(Path.GetDirectoryName(original), Path.GetDirectoryName(copy));
        Assert.NotEqual(playing, copy);
        Assert.Equal("theirs", File.ReadAllText(theirs));
        Assert.Equal("first pass", File.ReadAllText(playing));
        Assert.Equal("second pass", File.ReadAllText(copy));
        Assert.Equal("original", File.ReadAllText(original));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_a_copy_refuses_anything_not_named_as_ours_or_not_beside_the_original()
    {
        // Arrange
        var original = Write("Movie.en.srt", "original");
        var theirs = Write("Movie.en.synced.srt", "theirs");
        var elsewhere = Path.Combine(_root, "other");
        Directory.CreateDirectory(elsewhere);
        var foreign = Path.Combine(elsewhere, Path.GetFileName(await _store.WriteSyncedAsync(original, [1])));
        File.WriteAllText(foreign, "not beside the original");
        var ours = await _store.WriteSyncedAsync(original, [2]);

        // Act
        await _store.DeleteSyncedAsync(theirs, original);
        await _store.DeleteSyncedAsync(original, original);
        await _store.DeleteSyncedAsync(foreign, original);
        await _store.DeleteSyncedAsync(ours, original);

        // Assert
        Assert.True(File.Exists(theirs));
        Assert.True(File.Exists(original));
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(ours));
    }

    [Fact]
    public async Task A_read_stops_at_the_cap_and_an_oversized_file_reads_as_nothing()
    {
        var small = Write("Small.en.srt", "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var big = Path.Combine(_root, "Big.en.srt");
        await File.WriteAllBytesAsync(big, new byte[(8 * 1024 * 1024) + 1]);

        Assert.NotNull(await _store.ReadAsync(small));
        Assert.Null(await _store.ReadAsync(big));
        Assert.Null(await _store.ReadAsync(Path.Combine(_root, "Missing.en.srt")));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }
}
