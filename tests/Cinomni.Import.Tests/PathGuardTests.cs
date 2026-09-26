using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>Pure unit tests for the path-confinement guard.</summary>
public sealed class PathGuardTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\data\library" : "/data/library";

    [Fact]
    public void Confine_accepts_a_path_inside_the_root()
    {
        var inside = Path.Combine(Root, "Movie (2024)", "movie.mkv");

        var confined = PathGuard.Confine(Root, inside);

        Assert.NotNull(confined);
        Assert.True(PathGuard.IsWithin(Root, inside));
    }

    [Fact]
    public void Confine_rejects_parent_directory_traversal()
    {
        var escape = Path.Combine(Root, "..", "etc", "passwd");

        Assert.Null(PathGuard.Confine(Root, escape));
    }

    [Fact]
    public void Confine_rejects_a_sibling_outside_the_root()
    {
        var sibling = OperatingSystem.IsWindows() ? @"C:\data\other\movie.mkv" : "/data/other/movie.mkv";

        Assert.Null(PathGuard.Confine(Root, sibling));
    }

    [Fact]
    public void Confine_rejects_unc_and_device_paths()
    {
        Assert.Null(PathGuard.Confine(Root, @"\\server\share\movie.mkv"));
        Assert.Null(PathGuard.Confine(Root, "//server/share/movie.mkv"));
    }

    [Fact]
    public void Confine_accepts_the_root_itself()
    {
        Assert.NotNull(PathGuard.Confine(Root, Root));
    }

    [Fact]
    public void SanitizeName_replaces_invalid_characters()
    {
        var sanitized = PathGuard.SanitizeName("mo<vie>:\"/name?.mkv");

        Assert.DoesNotContain('<', sanitized);
        Assert.DoesNotContain('>', sanitized);
        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('?', sanitized);
    }

    [Fact]
    public void SanitizeName_replaces_the_whole_deny_set_on_every_platform()
    {
        // The deny set must not be the running OS's opinion. Path.GetInvalidFileNameChars() reports 41
        // characters on Windows but only '\0' and '/' on Unix, so sourcing it from the OS turned the
        // sanitiser into a near no-op on the one platform Cinomni ships — for names taken verbatim
        // from release titles and provider metadata. The library tree is served over SMB to Windows
        // and macOS clients, where such a name is unrepresentable, and a raw CR/LF in a name corrupts
        // any line-oriented tool that echoes the path.
        var denied = "<>:\"/\\|?*".Concat(Enumerable.Range(0, 32).Select(code => (char)code)).ToArray();

        var survivors = denied
            .Where(c => PathGuard.SanitizeName($"movie{c}name.mkv") != "movie_name.mkv")
            .Select(c => $"U+{(int)c:X4}")
            .ToArray();

        Assert.Empty(survivors);
    }

    [Fact]
    public void SanitizeName_keeps_characters_that_are_legal_everywhere()
    {
        // The deny set is fixed, not widened: accented titles, brackets and dots are legal on every
        // target filesystem and renaming them would rewrite paths already on disk.
        const string name = "Amélie (2001) [1080p] - Épisode #1, part 2.mkv";

        Assert.Equal(name, PathGuard.SanitizeName(name));
    }

    [Fact]
    public void SanitizeName_guards_reserved_names_and_trailing_dots()
    {
        Assert.StartsWith("_", PathGuard.SanitizeName("CON.mkv"));
        Assert.False(PathGuard.SanitizeName("name...").EndsWith('.'));
    }

    [Fact]
    public void SanitizeName_caps_length()
    {
        var sanitized = PathGuard.SanitizeName(new string('a', 500), maxLength: 100);

        Assert.True(sanitized.Length <= 100);
    }
}

/// <summary>What a download is scanned from must be a folder of its own, never the staging root itself.</summary>
public sealed class PathGuardStrictTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cinomni-staging");

    [Fact]
    public void A_folder_inside_the_root_is_strictly_within_it() =>
        Assert.True(PathGuard.IsStrictlyWithin(Root, Path.Combine(Root, "Movie.2024")));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/.")]
    [InlineData("/Movie.2024/..")]
    public void The_root_itself_however_it_is_spelled_is_not(string suffix) =>
        Assert.False(PathGuard.IsStrictlyWithin(Root, Root + suffix.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void A_sibling_of_the_root_is_not() =>
        Assert.False(PathGuard.IsStrictlyWithin(Root, Root + "-other"));
}
