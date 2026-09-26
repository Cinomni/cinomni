using Cinomni.Import.Files;

namespace Cinomni.Import.Tests;

/// <summary>
/// What the machine running the suite can actually do, asked once per run.
/// <para>
/// xUnit 2.9.3 cannot report a test as skipped from inside its body, so a test whose precondition the
/// machine does not meet used to <c>return</c> early — and a test that returns before it asserts is
/// reported as <b>passed</b>. A green run that proves nothing is worse than a red one, so the decision
/// belongs to the attribute, where the runner records it as a skip with its reason.
/// </para>
/// </summary>
internal static class TestMachine
{
    /// <summary>
    /// Whether the filesystem behind the temporary directory — where every real-disk test in this project
    /// puts its files — can hardlink at all. Asked once, by linking a zero-byte probe file between two
    /// directories on it, because attempting the link and reading the error is the only honest way to ask.
    /// </summary>
    public static bool CanHardlink => LazyCanHardlink.Value;

    /// <summary>
    /// Why this account cannot create a symbolic link, or null when it can — on Windows it takes developer
    /// mode or elevation. Asked once, by creating one.
    /// </summary>
    public static string? SymbolicLinkRefusal => LazySymbolicLinkRefusal.Value;

    private static readonly Lazy<string?> LazySymbolicLinkRefusal = new(ProbeSymbolicLink);

    private static string? ProbeSymbolicLink()
    {
        var probe = Directory.CreateTempSubdirectory("cinomni-symlink-probe-");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(probe.FullName, "link"), probe.CreateSubdirectory("target").FullName);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
        finally
        {
            try
            {
                probe.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory must never fail a test run.
            }
        }
    }

    private static readonly Lazy<bool> LazyCanHardlink = new(ProbeHardlink);

    private static bool ProbeHardlink()
    {
        var probe = Directory.CreateTempSubdirectory("cinomni-hardlink-probe-");
        try
        {
            return Hardlinks.Probe(
                probe.CreateSubdirectory("source").FullName,
                probe.CreateSubdirectory("target").FullName).CanHardlink;
        }
        finally
        {
            try
            {
                probe.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory must never fail a test run.
            }
        }
    }
}

/// <summary>
/// A fact that needs the temporary directory's filesystem to support hardlinks, because it arranges a
/// file that is already hardlinked and then asks the adapter what put it there.
/// </summary>
public sealed class HardlinkFactAttribute : FactAttribute
{
    public HardlinkFactAttribute()
    {
        if (!TestMachine.CanHardlink)
        {
            Skip = "The filesystem behind the temporary directory cannot hardlink, so a landed hardlink "
                + "cannot be arranged on it. The deployment target (one Linux filesystem) always can.";
        }
    }
}

/// <summary>
/// A fact that needs hardlinks <em>and</em> Unix permissions: expressing "no new files may be created
/// here" takes one <c>chmod</c> on Unix and an explicit deny ACE on Windows, and the deployment target
/// is Linux.
/// </summary>
public sealed class UnixHardlinkFactAttribute : FactAttribute
{
    public UnixHardlinkFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Taking write permission away from a directory is a Unix file mode; on Windows it "
                + "takes an explicit deny ACE, and the deployment target is Linux.";
        }
        else if (!TestMachine.CanHardlink)
        {
            Skip = "The filesystem behind the temporary directory cannot hardlink, so a landed hardlink "
                + "cannot be arranged on it.";
        }
    }
}

/// <summary>
/// A fact that needs Windows, because Windows is the platform on which a file's identity can be refused
/// to this process on purpose — an exclusive share lock does it, deterministically and with no
/// privileges. The same refusal happens on Linux (a path that goes away between the size check and the
/// identity read, among others), but only as a race a test cannot arrange.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Only Windows can refuse a file's identity to this process on demand, through an "
                + "exclusive share lock. On Linux the same outcome is a race, not an arrangement.";
        }
    }
}

/// <summary>A fact that needs to create symbolic links, which this account may not be allowed to do.</summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (TestMachine.SymbolicLinkRefusal is { } refusal)
        {
            Skip = $"This account cannot create a symbolic link ({refusal}). On Windows it takes developer mode or elevation.";
        }
    }
}
