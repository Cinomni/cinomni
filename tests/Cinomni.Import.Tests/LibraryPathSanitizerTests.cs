using Cinomni.Import.Application;
using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>
/// Pure unit tests for the repair rule applied to names an earlier build already wrote. Two things
/// matter here and they pull in opposite directions: every segment carrying a forbidden character has
/// to be repaired, and nothing else may be touched — this pass renames files in a working library, so
/// every rename it proposes has to be one the household would otherwise lose the file to.
/// </summary>
public sealed class LibraryPathSanitizerTests
{
    private const string LibraryRoot = "/data/test-library";

    private static readonly LibraryPathSanitizer Sanitizer = new(new ImportOptions { LibraryRoot = LibraryRoot });

    private static string Under(params string[] segments) =>
        Path.Combine([Path.GetFullPath(LibraryRoot), .. segments]);

    private static IReadOnlyList<string> TailSegments(string path, int count) =>
        path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[^count..];

    [Fact]
    public void A_file_name_carrying_forbidden_characters_is_repaired()
    {
        var current = Under("Movie", "Alien: Director's Cut.mkv");

        var sanitised = Sanitizer.SanitisedPathFor(current);

        Assert.NotNull(sanitised);
        Assert.Equal("Alien_ Director's Cut.mkv", TailSegments(sanitised!, 1)[0]);
    }

    [Fact]
    public void A_directory_is_repaired_and_not_only_the_leaf()
    {
        // The series layout is two directories deep and both come from provider text, so a season
        // folder carries exactly the characters the file name does.
        var current = Under("Doctor Who: Series?", "Season 01", "episode.mkv");

        var sanitised = Sanitizer.SanitisedPathFor(current);

        Assert.NotNull(sanitised);
        Assert.Equal(
            ["Doctor Who_ Series_", "Season 01", "episode.mkv"],
            TailSegments(sanitised!, 3));
    }

    [Fact]
    public void A_name_that_is_already_clean_is_left_alone()
    {
        var current = Under("The Wire (2002)", "Season 01", "The Wire - S01E01 - The Target.mkv");

        Assert.Null(Sanitizer.SanitisedPathFor(current));
    }

    [Fact]
    public void A_long_name_is_not_truncated()
    {
        // The one rule this pass must NOT re-apply. A 300-character name is legal on every filesystem
        // Cinomni runs on; renaming it would be the repair inventing work, and it would break every
        // player bookmark and sidecar naming that already points at the file.
        var longName = new string('a', 300) + ".mkv";
        var current = Under("Movie", longName);

        Assert.Null(Sanitizer.SanitisedPathFor(current));
    }

    [Fact]
    public void A_control_character_is_repaired()
    {
        // A raw newline in a name corrupts any line-oriented tool that echoes the path, and it reaches
        // one: the import log lines carry library paths.
        var current = Under("Movie", "Two\nLines.mkv");

        var sanitised = Sanitizer.SanitisedPathFor(current);

        Assert.NotNull(sanitised);
        Assert.Equal("Two_Lines.mkv", TailSegments(sanitised!, 1)[0]);
    }

    [Fact]
    public void A_path_outside_the_library_is_reported_as_nothing_to_do()
    {
        // Not "repair it where it lies". A stored path outside the root is either a misconfiguration or
        // a tampered row, and renaming a file the library does not own is the one mistake a repair pass
        // must never make.
        var outside = OperatingSystem.IsWindows() ? @"C:\elsewhere\Alien: Cut.mkv" : "/elsewhere/Alien: Cut.mkv";

        Assert.Null(Sanitizer.SanitisedPathFor(outside));
    }

    [Fact]
    public void A_traversal_in_a_stored_path_is_refused()
    {
        Assert.Null(Sanitizer.SanitisedPathFor(Under("..", "escaped.mkv")));
    }

    [Fact]
    public void The_repaired_path_stays_inside_the_library_root()
    {
        var current = Under("Movie:", "file?.mkv");

        var sanitised = Sanitizer.SanitisedPathFor(current);

        Assert.NotNull(sanitised);
        Assert.True(PathGuard.IsWithin(LibraryRoot, sanitised!));
    }
}
