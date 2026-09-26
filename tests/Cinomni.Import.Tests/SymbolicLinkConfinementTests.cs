using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>
/// A torrent can create symbolic links in staging (libtorrent honours the attribute). Neither the guard
/// nor the scan may let one carry an import out of its root: the text of a path does not show where a
/// link inside it leads.
/// </summary>
public sealed class SymbolicLinkConfinementTests : IDisposable
{
    private readonly DirectoryInfo _sandbox = Directory.CreateTempSubdirectory("cinomni-symlink-");

    private string Root => Path.Combine(_sandbox.FullName, "staging");

    private string Outside => Path.Combine(_sandbox.FullName, "outside");

    public SymbolicLinkConfinementTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.mkv"), "not this download's");
    }

    public void Dispose()
    {
        try
        {
            _sandbox.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [SymbolicLinkFact]
    public void A_directory_link_pointing_out_of_the_root_is_refused()
    {
        Directory.CreateSymbolicLink(Path.Combine(Root, "escape"), Outside);

        Assert.Null(PathGuard.Confine(Root, Path.Combine(Root, "escape", "secret.mkv")));
        Assert.Null(PathGuard.Confine(Root, Path.Combine(Root, "escape", "not-yet-there.mkv")));
    }

    [SymbolicLinkFact]
    public void A_file_link_pointing_out_of_the_root_is_refused()
    {
        File.CreateSymbolicLink(Path.Combine(Root, "movie.mkv"), Path.Combine(Outside, "secret.mkv"));

        Assert.Null(PathGuard.Confine(Root, Path.Combine(Root, "movie.mkv")));
    }

    [SymbolicLinkFact]
    public void A_link_that_stays_inside_the_root_is_accepted_and_the_path_is_returned_as_written()
    {
        var real = Directory.CreateDirectory(Path.Combine(Root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "alias"), real.FullName);
        var candidate = Path.Combine(Root, "alias", "movie.mkv");

        Assert.Equal(Path.GetFullPath(candidate), PathGuard.Confine(Root, candidate));
    }

    [SymbolicLinkFact]
    public void A_root_reached_through_a_link_of_its_own_still_works()
    {
        var linkedRoot = Path.Combine(_sandbox.FullName, "linked-staging");
        Directory.CreateSymbolicLink(linkedRoot, Root);
        var candidate = Path.Combine(linkedRoot, "Movie", "movie.mkv");

        Assert.Equal(Path.GetFullPath(candidate), PathGuard.Confine(linkedRoot, candidate));
    }

    [SymbolicLinkFact]
    public void The_scan_of_a_download_does_not_follow_links()
    {
        var download = Directory.CreateDirectory(Path.Combine(Root, "download"));
        File.WriteAllText(Path.Combine(download.FullName, "movie.mkv"), "the download");
        Directory.CreateSymbolicLink(Path.Combine(download.FullName, "extras"), Outside);
        File.CreateSymbolicLink(Path.Combine(download.FullName, "bonus.mkv"), Path.Combine(Outside, "secret.mkv"));

        var files = new LocalImportFileSystem().EnumerateFiles(download.FullName);

        var only = Assert.Single(files);
        Assert.Equal(Path.Combine(download.FullName, "movie.mkv"), only.Path);
    }

    [SymbolicLinkFact]
    public void A_download_folder_that_is_a_link_to_the_staging_root_is_not_strictly_inside_it()
    {
        // `..` by another name: scanning it would scan every download.
        var link = Path.Combine(Root, "download");
        Directory.CreateSymbolicLink(link, "..");
        Directory.CreateSymbolicLink(Path.Combine(Root, "same-root"), Root);

        Assert.False(PathGuard.IsStrictlyWithin(Root, Path.Combine(Root, "same-root")));
        Assert.Empty(new LocalImportFileSystem().EnumerateFiles(link));
    }

    [SymbolicLinkFact]
    public void A_download_folder_that_is_a_link_to_another_download_is_not_walked()
    {
        var other = Directory.CreateDirectory(Path.Combine(Root, "other"));
        File.WriteAllText(Path.Combine(other.FullName, "theirs.mkv"), "another download");
        var link = Path.Combine(Root, "mine");
        Directory.CreateSymbolicLink(link, other.FullName);

        Assert.Empty(new LocalImportFileSystem().EnumerateFiles(link));
    }

    [SymbolicLinkFact]
    public void A_link_target_passing_through_another_link_is_followed_before_its_parent_step()
    {
        // `s/../x`, with s a link deep outside the root: the kernel applies `..` after following s, so
        // the path lands beside s's target, not inside the root where the text suggests.
        var deep = Directory.CreateDirectory(Path.Combine(Outside, "deep"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "s"), deep.FullName);
        File.CreateSymbolicLink(Path.Combine(Root, "escape.mkv"), Path.Combine("s", "..", "secret.mkv"));

        Assert.Null(PathGuard.Confine(Root, Path.Combine(Root, "escape.mkv")));
    }

    [SymbolicLinkFact]
    public async Task A_landing_whose_source_became_a_link_is_refused()
    {
        var source = Path.Combine(Root, "movie.mkv");
        File.CreateSymbolicLink(source, Path.Combine(Outside, "secret.mkv"));
        var destination = Path.Combine(_sandbox.FullName, "library-movie.mkv");

        await Assert.ThrowsAsync<IOException>(() => new LocalImportFileSystem().HardlinkOrCopyAsync(source, destination));
        Assert.False(File.Exists(destination));
    }

    [SymbolicLinkFact]
    public void Path_repair_still_refuses_to_move_a_file_that_leads_out_of_the_library()
    {
        // The repair pass judges names on text alone, and consults the disk only for a path it would
        // move: that check must still see a link leading out.
        Directory.CreateSymbolicLink(Path.Combine(Root, "Films"), Outside);
        var sanitizer = new LibraryPathSanitizer(new Application.ImportOptions { LibraryRoot = Root });

        Assert.Null(sanitizer.SanitisedPathFor(Path.Combine(Root, "Films", "Bad:Name.mkv")));
        Assert.NotNull(sanitizer.SanitisedPathFor(Path.Combine(Root, "Real", "Bad:Name.mkv")));
    }

    [SymbolicLinkFact]
    public void A_single_file_download_that_is_a_link_has_no_content()
    {
        var link = Path.Combine(Root, "movie.mkv");
        File.CreateSymbolicLink(link, Path.Combine(Outside, "secret.mkv"));

        Assert.Empty(new LocalImportFileSystem().EnumerateFiles(link));
    }
}
