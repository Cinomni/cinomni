using System.Runtime.Versioning;
using Cinomni.Operations.Backup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Retention is the only code in the product that removes data nobody asked it to remove, so this suite
/// is mostly about what it must <b>not</b> touch: a foreign file, a nested directory, a file whose name
/// only looks like a backup, and a dump that is still being written.
/// </summary>
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cinomni-backup-store-{Guid.NewGuid():N}");
    private readonly BackupStore _store;

    public BackupStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new BackupStore(
            new BackupOptions { Root = Path.GetFullPath(_root) }, NullLogger<BackupStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    /// <summary>
    /// Taken from the wall clock rather than pinned to a literal: the incomplete-artefact sweep compares
    /// against a file's real modification time, so a hard-coded instant would make the suite depend on
    /// which side of that date the machine running it happens to be.
    /// </summary>
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>Writes a complete set (dump plus manifest) stamped <paramref name="daysAgo"/> days back.</summary>
    private string WriteBackup(int daysAgo)
    {
        var stamp = BackupStore.StampFor(Now.AddDays(-daysAgo));
        File.WriteAllText(_store.DumpPathFor(stamp), $"dump-{stamp}");
        File.WriteAllText(_store.ManifestPathFor(stamp), "{}");
        return stamp;
    }

    private void Age(string path, TimeSpan by) => File.SetLastWriteTimeUtc(path, (Now - by).UtcDateTime);

    [Fact]
    public void Retention_keeps_the_newest_sets_and_removes_the_rest()
    {
        var stamps = Enumerable.Range(0, 5).Select(WriteBackup).ToArray();

        _store.ApplyRetention(keepCount: 2, Now);

        var remaining = _store.List().Select(set => set.Stamp).ToArray();
        Assert.Equal([stamps[0], stamps[1]], remaining);
        Assert.False(File.Exists(_store.DumpPathFor(stamps[2])));
        Assert.False(File.Exists(_store.ManifestPathFor(stamps[2])));
    }

    [Fact]
    public void Retention_removes_nothing_when_there_are_fewer_sets_than_it_keeps()
    {
        WriteBackup(0);
        WriteBackup(1);

        var removed = _store.ApplyRetention(keepCount: 7, Now);

        Assert.Equal(0, removed);
        Assert.Equal(2, _store.List().Count);
    }

    [Fact]
    public void Keeping_zero_backups_is_rejected_rather_than_emptying_the_directory()
    {
        WriteBackup(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => _store.ApplyRetention(keepCount: 0, Now));
        Assert.Single(_store.List());
    }

    /// <summary>The backup root is the operator's directory too; retention may only see what it wrote.</summary>
    [Fact]
    public void A_file_the_store_did_not_write_survives_retention_untouched()
    {
        Enumerable.Range(0, 3).ToList().ForEach(days => WriteBackup(days));

        var foreign = Path.Combine(_root, "important.txt");
        var wrongPrefix = Path.Combine(_root, "backup-20260101T000000Z.dump");
        var wrongStamp = Path.Combine(_root, "cinomni-not-a-stamp.dump");
        var offSiteCopy = Path.Combine(_root, "cinomni-20260101T000000Z.dump.gpg");

        foreach (var path in new[] { foreign, wrongPrefix, wrongStamp, offSiteCopy })
        {
            File.WriteAllText(path, "keep me");
            Age(path, TimeSpan.FromDays(400));
        }

        _store.ApplyRetention(keepCount: 1, Now);

        Assert.All(new[] { foreign, wrongPrefix, wrongStamp, offSiteCopy }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void A_directory_inside_the_backup_root_is_never_a_deletion_candidate()
    {
        WriteBackup(0);
        var nested = Path.Combine(_root, "cinomni-20260101T000000Z.dump");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "content.bin"), "kept");

        _store.ApplyRetention(keepCount: 1, Now);

        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(Path.Combine(nested, "content.bin")));
    }

    /// <summary>
    /// An unpaired dump is a crashed run, not a backup: counting it would let a broken run push a good
    /// backup out of the retention window.
    /// </summary>
    [Fact]
    public void An_unpaired_dump_is_not_a_backup_and_never_counts_towards_the_kept_total()
    {
        var good = WriteBackup(0);
        var orphan = BackupStore.StampFor(Now.AddDays(-1));
        File.WriteAllText(_store.DumpPathFor(orphan), "half a run");

        var sets = _store.List();

        Assert.Single(sets);
        Assert.Equal(good, sets[0].Stamp);
    }

    /// <summary>A dump still being written must survive; the same file, a day later, must not.</summary>
    [Fact]
    public void A_partial_file_is_invisible_until_it_is_old_enough_to_be_certainly_abandoned()
    {
        WriteBackup(0);
        // A stamp of its own: a partial belonging to a set that already completed is not an abandoned run.
        var partial = _store.PartialPathFor(BackupStore.StampFor(Now.AddHours(-3)));
        File.WriteAllText(partial, "still writing");

        _store.ApplyRetention(keepCount: 1, Now);
        Assert.True(File.Exists(partial));

        Age(partial, TimeSpan.FromDays(2));
        _store.ApplyRetention(keepCount: 1, Now);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public void An_abandoned_unpaired_dump_is_swept_once_it_is_old_enough()
    {
        WriteBackup(0);
        var orphan = BackupStore.StampFor(Now.AddDays(-30));
        var orphanPath = _store.DumpPathFor(orphan);
        File.WriteAllText(orphanPath, "half a run");
        Age(orphanPath, TimeSpan.FromDays(30));

        _store.ApplyRetention(keepCount: 7, Now);

        Assert.False(File.Exists(orphanPath));
    }

    [Fact]
    public void Publishing_renames_the_partial_into_place_so_a_backup_appears_atomically()
    {
        var stamp = BackupStore.StampFor(Now);
        var partial = _store.PartialPathFor(stamp);
        File.WriteAllText(partial, "finished");
        File.WriteAllText(_store.ManifestPathFor(stamp), "{}");

        Assert.Empty(_store.List());

        BackupStore.Publish(partial, _store.DumpPathFor(stamp));

        Assert.False(File.Exists(partial));
        Assert.Single(_store.List());
    }

    [Fact]
    public void A_backup_is_found_by_stamp_by_dump_name_and_by_manifest_name()
    {
        var stamp = WriteBackup(0);

        Assert.Equal(stamp, _store.Find(stamp)?.Stamp);
        Assert.Equal(stamp, _store.Find($"cinomni-{stamp}.dump")?.Stamp);
        Assert.Equal(stamp, _store.Find($"cinomni-{stamp}.manifest.json")?.Stamp);
    }

    /// <summary>
    /// A name is never a path. Anything that is not one of this store's own names resolves to nothing,
    /// so a caller cannot address a file outside the root even by accident.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../etc/passwd")]
    [InlineData(@"..\..\windows\system32\config\SAM")]
    [InlineData("/etc/shadow")]
    [InlineData("cinomni-*.dump")]
    [InlineData("not-a-stamp")]
    public void A_name_that_is_not_one_this_store_issues_resolves_to_nothing(string name)
    {
        WriteBackup(0);

        Assert.Null(_store.Find(name));
    }

    [Fact]
    public async Task The_hash_of_a_dump_is_its_content_hash()
    {
        var path = Path.Combine(_root, "content.bin");
        await File.WriteAllBytesAsync(path, "abc"u8.ToArray());

        var hash = await BackupStore.ComputeSha256Async(path);

        // SHA-256("abc"), lower-case hexadecimal.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    /// <summary>
    /// A dump is a credential store. On Unix it is created readable and writable by this account only;
    /// on Windows the call is a no-op and the directory's own inheritance governs, which the deployment
    /// documentation says plainly rather than implying a guarantee that is not there.
    /// </summary>
    // The attribute is what keeps this off Windows; the annotation is how the platform analyser is told.
    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void A_published_dump_is_readable_by_this_account_only()
    {
        var stamp = BackupStore.StampFor(Now);
        var partial = _store.PartialPathFor(stamp);
        File.WriteAllText(partial, "secrets");

        BackupStore.Publish(partial, _store.DumpPathFor(stamp));

        var mode = File.GetUnixFileMode(_store.DumpPathFor(stamp));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void An_absent_backup_root_lists_as_empty_rather_than_throwing()
    {
        var store = new BackupStore(
            new BackupOptions { Root = Path.Combine(_root, "not-created-yet") },
            NullLogger<BackupStore>.Instance);

        Assert.Empty(store.List());
        Assert.Null(store.Find("20260729T120000Z"));
    }

    [Fact]
    public void Ensuring_the_root_creates_it_and_is_repeatable()
    {
        var target = Path.Combine(_root, "nested", "backups");
        var store = new BackupStore(new BackupOptions { Root = target }, NullLogger<BackupStore>.Instance);

        Assert.True(store.EnsureRoot().IsSuccess);
        Assert.True(store.EnsureRoot().IsSuccess);

        Assert.True(Directory.Exists(target));
    }

    /// <summary>
    /// A backup root that cannot be created is the ordinary bind-mount failure — a directory owned by
    /// another account. It has to come back as a result the run can record and the CLI can print, not as
    /// an exception out of a background worker.
    /// </summary>
    [Fact]
    public void A_root_that_cannot_be_created_is_reported_rather_than_thrown()
    {
        var occupied = Path.Combine(_root, "in-the-way");
        File.WriteAllText(occupied, "a file where the directory should be");

        var store = new BackupStore(new BackupOptions { Root = occupied }, NullLogger<BackupStore>.Instance);

        var outcome = store.EnsureRoot();

        Assert.True(outcome.IsFailure);
        Assert.Equal(BackupStore.RootUnwritableCode, outcome.Error.Code);
        Assert.Contains("Backup:Root", outcome.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sweep on its own, because a run calls it before it writes rather than only after it succeeds:
    /// an installation whose disk filled up would otherwise never reclaim the partials that are the
    /// reason it cannot succeed.
    /// </summary>
    [Fact]
    public void Sweeping_reclaims_abandoned_partials_without_a_successful_run()
    {
        var abandoned = Enumerable.Range(1, 3)
            .Select(hours =>
            {
                var path = _store.PartialPathFor(BackupStore.StampFor(Now.AddDays(-2).AddHours(-hours)));
                File.WriteAllText(path, "half a dump");
                Age(path, TimeSpan.FromDays(2));
                return path;
            })
            .ToArray();

        var live = _store.PartialPathFor(BackupStore.StampFor(Now));
        File.WriteAllText(live, "still writing");

        var removed = _store.SweepIncomplete(Now);

        Assert.Equal(3, removed);
        Assert.All(abandoned, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(live));
    }
}
