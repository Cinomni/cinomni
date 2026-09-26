using Cinomni.Operations.Backup;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Where a backup is allowed to live, and what stops the installation when it is not.
/// <para>
/// The overlap rule is the security-relevant one: a dump carries delivery-channel URLs, indexer
/// addresses that commonly embed keys, Argon2id password verifiers and session token hashes, so the
/// directory it lands in must not be one the library or playback surfaces read from — and must not be
/// one the retention sweep could reach media through.
/// </para>
/// </summary>
public sealed class BackupOptionsTests
{
    private static string Absolute(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.GetTempPath(), .. segments]));

    [Fact]
    public void A_relative_root_is_resolved_to_an_absolute_path_so_one_spelling_is_logged_and_compared()
    {
        var options = new BackupOptions { Root = "./.backups" };

        options.Validate();

        Assert.True(Path.IsPathFullyQualified(options.Root));
        Assert.EndsWith(".backups", options.Root, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share\backups")]
    [InlineData("//server/share/backups")]
    public void A_root_that_cannot_name_a_local_directory_stops_startup(string root)
    {
        var options = new BackupOptions { Root = root };

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Backup:Root", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeping_fewer_than_one_backup_stops_startup()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            new BackupOptions { Root = Absolute("cinomni-backups"), KeepCount = 0 }.Validate);

        Assert.Contains("KeepCount", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Interval")]
    [InlineData("Timeout")]
    public void A_non_positive_duration_stops_startup(string key)
    {
        var options = new BackupOptions { Root = Absolute("cinomni-backups") };
        if (key == "Interval")
        {
            options.Interval = TimeSpan.Zero;
        }
        else
        {
            options.Timeout = TimeSpan.Zero;
        }

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_tool_path_stops_startup()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            new BackupOptions { Root = Absolute("cinomni-backups"), PgDumpPath = " " }.Validate);

        Assert.Contains("PgDumpPath", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A dump inside the library would be a credential store in a directory the media surfaces serve.</summary>
    [Fact]
    public void A_backup_root_inside_a_storage_root_is_refused_naming_both()
    {
        var library = Absolute("cinomni-media", "library");
        var backupRoot = BackupRoot.Canonicalize("Backup:Root", Path.Combine(library, "backups"));

        var failure = Assert.Throws<InvalidOperationException>(
            () => BackupRoot.VerifySeparateFrom(backupRoot, [("Import:LibraryRoot", library)]));

        Assert.Contains("Import:LibraryRoot", failure.Message, StringComparison.Ordinal);
        Assert.Contains(backupRoot, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The traversal spelling of the same mistake must not slip past the canonical comparison.</summary>
    [Fact]
    public void A_backup_root_that_only_reaches_a_storage_root_after_canonicalisation_is_still_refused()
    {
        var library = Absolute("cinomni-media", "library");
        var sneaky = Path.Combine(library, "..", "library", "backups");
        var backupRoot = BackupRoot.Canonicalize("Backup:Root", sneaky);

        Assert.Throws<InvalidOperationException>(
            () => BackupRoot.VerifySeparateFrom(backupRoot, [("Import:LibraryRoot", library)]));
    }

    /// <summary>The other direction: retention deletes inside its own root, so no media may be nested under it.</summary>
    [Fact]
    public void A_storage_root_nested_under_the_backup_root_is_refused()
    {
        var backupRoot = Absolute("cinomni-backups");
        var transcodes = Path.Combine(backupRoot, "transcodes");

        var failure = Assert.Throws<InvalidOperationException>(
            () => BackupRoot.VerifySeparateFrom(backupRoot, [("Playback:TranscodeRoot", transcodes)]));

        Assert.Contains("Playback:TranscodeRoot", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sibling_roots_under_the_same_parent_are_accepted()
    {
        var backupRoot = BackupRoot.Canonicalize("Backup:Root", Absolute("cinomni-data", "backups"));

        BackupRoot.VerifySeparateFrom(
            backupRoot,
            [
                ("Import:LibraryRoot", Absolute("cinomni-data", "library")),
                ("Downloads:Sidecar:StagingPath", Absolute("cinomni-data", "downloads")),
                ("Playback:TranscodeRoot", Absolute("cinomni-data", "transcodes")),
            ]);
    }

    /// <summary>A root that shares a name prefix is not a parent — "library-old" is not inside "library".</summary>
    [Fact]
    public void A_name_prefix_is_not_containment()
    {
        var library = Absolute("cinomni-data", "library");
        var backupRoot = BackupRoot.Canonicalize("Backup:Root", Absolute("cinomni-data", "library-backups"));

        BackupRoot.VerifySeparateFrom(backupRoot, [("Import:LibraryRoot", library)]);

        Assert.False(BackupRoot.Contains(library, backupRoot));
    }

    /// <summary>An unset storage root cannot make the check pass or fail by accident.</summary>
    [Fact]
    public void An_unset_storage_root_is_skipped_rather_than_matching_everything()
    {
        var backupRoot = Absolute("cinomni-backups");

        BackupRoot.VerifySeparateFrom(backupRoot, [("Import:LibraryRoot", "  ")]);
    }

    /// <summary>
    /// A backup root that is a symbolic link into the library is the same mistake wearing a disguise, and
    /// lexical path arithmetic cannot see it. On a bare-metal installation this is how the dumps end up
    /// inside a served tree while startup reports nothing.
    /// </summary>
    [SymbolicLinkFact]
    public void A_backup_root_that_is_a_link_into_a_storage_root_is_refused()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"cinomni-link-{Guid.NewGuid():N}");
        var library = Path.Combine(temporary, "library");
        var target = Path.Combine(library, "backups");
        var link = Path.Combine(temporary, "backups");

        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        try
        {
            var backupRoot = BackupRoot.Canonicalize("Backup:Root", link);

            Assert.Throws<InvalidOperationException>(
                () => BackupRoot.VerifySeparateFrom(backupRoot, [("Import:LibraryRoot", library)]));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(temporary, recursive: true);
        }
    }
}
