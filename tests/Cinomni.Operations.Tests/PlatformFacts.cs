namespace Cinomni.Operations.Tests;

/// <summary>
/// A fact about a guarantee only Unix makes. xUnit 2.9.3 cannot report a test as skipped from inside its
/// body, so the platform check used to be an early <c>return</c> — and a test that returns before it
/// asserts is reported as <b>passed</b>, which is a green result standing in for a check that never ran.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "This asserts a Unix file mode. On Windows the call that sets it is a no-op and the "
                + "directory's own inheritance governs, which the deployment documentation says plainly "
                + "rather than implying a guarantee that is not there.";
        }
    }
}

/// <summary>
/// A fact that needs to create a symbolic link, which is a privilege this account may not have — on
/// Windows it takes developer mode or elevation. Asked once, by creating one.
/// </summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (TestMachine.SymbolicLinkRefusal is { } refusal)
        {
            Skip = $"This account cannot create a symbolic link, so nothing about the check is proven "
                + $"({refusal}). On Windows it takes developer mode or elevation.";
        }
    }
}

/// <summary>What the machine running the suite can actually do, asked once per run.</summary>
internal static class TestMachine
{
    /// <summary>
    /// Why this account cannot create a directory symbolic link, or <see langword="null"/> when it can.
    /// </summary>
    public static string? SymbolicLinkRefusal => LazySymbolicLinkRefusal.Value;

    private static readonly Lazy<string?> LazySymbolicLinkRefusal = new(ProbeSymbolicLink);

    private static string? ProbeSymbolicLink()
    {
        var probe = Directory.CreateTempSubdirectory("cinomni-symlink-probe-");
        try
        {
            Directory.CreateSymbolicLink(
                Path.Combine(probe.FullName, "link"),
                probe.CreateSubdirectory("target").FullName);
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
}
