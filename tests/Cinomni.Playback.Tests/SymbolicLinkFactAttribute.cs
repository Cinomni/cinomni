namespace Cinomni.Playback.Tests;

/// <summary>
/// A fact that needs to create a symbolic link, which is a privilege this account may not have — on
/// Windows it takes developer mode or elevation. Asked once, by creating one. Skipped rather than
/// returning early: xUnit 2 reports a test that returns before it asserts as passed.
/// </summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Refusal = new(Probe);

    public SymbolicLinkFactAttribute()
    {
        if (Refusal.Value is { } refusal)
        {
            Skip = $"This account cannot create a symbolic link ({refusal}). On Windows it takes developer mode or elevation.";
        }
    }

    private static string? Probe()
    {
        var probe = Directory.CreateTempSubdirectory("cinomni-playback-symlink-probe-");
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
}
