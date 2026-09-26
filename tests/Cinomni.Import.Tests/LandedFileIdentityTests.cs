using System.Runtime.Versioning;
using Cinomni.Import.Contracts;
using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>
/// The production adapter on the recovery path: an import job re-driven after a restart reaches a file
/// that is already where it belongs, and has to say what an earlier attempt did to it, because the
/// operation row is the household's own record of what happened to that file.
/// <para>
/// These run against real disk — the one place in this project's tests where the in-memory fake would
/// prove nothing, since the whole question is what the filesystem holds. Three rules are asserted: the
/// answer is about <em>this</em> file (a copy is never recorded as a hardlink, whatever the two
/// directories are capable of); asking costs no write, because the source directory belongs to a torrent
/// that is still seeding from it; and a platform that will not answer produces "unknown" rather than the
/// likelier of the two.
/// </para>
/// </summary>
public sealed class LandedFileIdentityTests : IDisposable
{
    private const string Contents = "the same bytes on both sides";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"cinomni-landed-identity-{Guid.NewGuid():N}");

    private readonly LocalImportFileSystem _fileSystem = new();

    private readonly string _stagingDirectory;
    private readonly string _libraryDirectory;
    private readonly string _sourcePath;
    private readonly string _targetPath;

    public LandedFileIdentityTests()
    {
        _stagingDirectory = Path.Combine(_root, "downloads");
        _libraryDirectory = Path.Combine(_root, "library");
        Directory.CreateDirectory(_stagingDirectory);
        Directory.CreateDirectory(_libraryDirectory);
        _sourcePath = Path.Combine(_stagingDirectory, "the.film.2019.1080p.mkv");
        _targetPath = Path.Combine(_libraryDirectory, "The Film (2019).mkv");
        File.WriteAllText(_sourcePath, Contents);
    }

    public void Dispose()
    {
        try
        {
            // Restore anything a test made read-only, or the tree cannot be removed.
            if (!OperatingSystem.IsWindows() && Directory.Exists(_stagingDirectory))
            {
                File.SetUnixFileMode(
                    _stagingDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    [Fact]
    public async Task A_file_an_earlier_attempt_copied_is_recorded_as_a_copy()
    {
        // The case the record must not get wrong: the two directories here are one filesystem, so
        // asking whether a hardlink is *possible* answers "yes" — and this file is still a copy. An
        // installation whose mount was fixed between the two attempts would otherwise be told the
        // bytes are shared when removing the download leaves the library holding the only copy.
        File.Copy(_sourcePath, _targetPath);

        var performed = await _fileSystem.HardlinkOrCopyAsync(_sourcePath, _targetPath);

        Assert.Equal(FileOperationType.Copy, performed);
    }

    [HardlinkFact]
    public async Task A_file_an_earlier_attempt_hardlinked_is_recorded_as_a_hardlink()
    {
        Assert.True(Hardlinks.TryLink(_sourcePath, _targetPath, out var errorCode), Hardlinks.Describe(errorCode));

        var performed = await _fileSystem.HardlinkOrCopyAsync(_sourcePath, _targetPath);

        Assert.Equal(FileOperationType.Hardlink, performed);
    }

    [Fact]
    public async Task Deciding_what_landed_leaves_nothing_behind_in_either_directory()
    {
        File.Copy(_sourcePath, _targetPath);

        await _fileSystem.HardlinkOrCopyAsync(_sourcePath, _targetPath);

        Assert.Equal([_sourcePath], Directory.GetFileSystemEntries(_stagingDirectory));
        Assert.Equal([_targetPath], Directory.GetFileSystemEntries(_libraryDirectory));
    }

    // The attribute is what keeps this off Windows; the annotation is how the platform analyser is told.
    [UnixHardlinkFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_landed_hardlink_is_still_recognised_when_the_staging_directory_admits_no_new_file()
    {
        // The seeding torrent's directory, proved by taking write permission away from it: deciding what
        // an earlier attempt did must be a question, not an operation.
        Assert.True(Hardlinks.TryLink(_sourcePath, _targetPath, out var errorCode), Hardlinks.Describe(errorCode));

        File.SetUnixFileMode(_stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var performed = await _fileSystem.HardlinkOrCopyAsync(_sourcePath, _targetPath);

        Assert.Equal(FileOperationType.Hardlink, performed);
    }

    /// <summary>
    /// The third answer, and the one the adapter was written believing it would never give: the file is in
    /// place and the platform will not say what put it there. It is reachable on the deployment target
    /// too — <c>AreSameFile</c> answers "unknown" whenever <em>either</em> path cannot be identified, and
    /// a source that goes away between the size check and the identity read does exactly that — but only
    /// as a race. Windows can arrange it on purpose, so this is where the mapping from "the platform
    /// refused" to a recorded <see cref="FileOperationType.Unknown"/> is proved against real disk.
    /// </summary>
    [WindowsFact]
    public async Task A_landed_file_whose_identity_the_platform_refuses_is_recorded_as_unknown()
    {
        File.Copy(_sourcePath, _targetPath);

        // An exclusive handle: the sizes still read, and the identity read — which has to open the file —
        // does not. Nothing about the file changes, which is the point.
        using (new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var performed = await _fileSystem.HardlinkOrCopyAsync(_sourcePath, _targetPath);

            Assert.Equal(FileOperationType.Unknown, performed);
        }

        // And it is a refusal, not a property of these two files: the same pair answers once it can be read.
        Assert.Equal(FileOperationType.Copy, await _fileSystem.HardlinkOrCopyAsync(_sourcePath, _targetPath));
    }

    /// <summary>
    /// The same "unknown" from the other side, on every platform: a path that is not there cannot be
    /// identified, and one unidentifiable side is enough. This is the shape the refusal takes on Linux —
    /// a file that went away between the check and the read — and it is why the outcome is a production
    /// one on the deployment target rather than a defensive branch.
    /// </summary>
    [Fact]
    public void One_unidentifiable_side_is_enough_for_the_answer_to_be_unknown()
    {
        var absent = Path.Combine(_stagingDirectory, "gone-between-the-two-reads.mkv");

        Assert.Null(Hardlinks.AreSameFile(_sourcePath, absent));
        Assert.Null(Hardlinks.AreSameFile(absent, _sourcePath));
        Assert.Null(Hardlinks.AreSameFile(absent, absent));
    }
}
