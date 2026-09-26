using System.Globalization;
using System.Reflection;

namespace Cinomni.Host.SystemInfo;

/// <summary>
/// What this build can honestly say about itself: the version it was compiled as, and — only when the
/// pipeline stamped them — the commit it came from and when it was built.
/// <para>
/// Every field is read from assembly attributes once, at first use, because none of them can change
/// while the process runs. A development build stamps nothing beyond its version, so
/// <see cref="Commit"/> and <see cref="BuildDate"/> are null there; that is a build that was not
/// stamped, never an error, and a caller is expected to render it as absent rather than as unknown.
/// </para>
/// </summary>
/// <param name="Version">The plain three-part number, e.g. <c>1.2.3</c>.</param>
/// <param name="InformationalVersion">
/// The full version including any prerelease and build metadata, e.g. <c>1.2.3-beta.1+a1b2c3d</c>.
/// This is the one that identifies a build without ambiguity; on a development build it is simply
/// the same string as <see cref="Version"/>.
/// </param>
/// <param name="Commit">The revision the build came from, or null when nothing stamped one.</param>
/// <param name="BuildDate">When the artefact was built, or null when nothing stamped one.</param>
public sealed record BuildInfo(string Version, string InformationalVersion, string? Commit, DateTimeOffset? BuildDate)
{
    /// <summary>The key the build pipeline writes the timestamp under; see <c>Cinomni.Host.csproj</c>.</summary>
    internal const string BuildDateKey = "BuildDate";

    private const string UnknownVersion = "0.0.0";

    /// <summary>This installation, read once. Nothing here can change while the process is alive.</summary>
    public static BuildInfo Current { get; } = Read(typeof(BuildInfo).Assembly);

    internal static BuildInfo Read(Assembly assembly) => From(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assembly.GetName().Version,
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == BuildDateKey)?.Value);

    /// <summary>
    /// What the three stamped values mean, apart from where they are read from. The split is what
    /// makes every case testable — a build with no commit, one whose metadata is not a revision, one
    /// with an unreadable date — without synthesising an assembly for each.
    /// </summary>
    internal static BuildInfo From(string? informationalVersion, Version? assemblyVersion, string? buildDate) =>
        new(ThreePartVersion(assemblyVersion, informationalVersion),
            string.IsNullOrWhiteSpace(informationalVersion)
                ? ThreePartVersion(assemblyVersion, null)
                : informationalVersion,
            CommitFrom(informationalVersion),
            BuildDateFrom(buildDate));

    private static string ThreePartVersion(Version? assemblyVersion, string? informational)
    {
        if (assemblyVersion is not null)
        {
            return new Version(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(assemblyVersion.Build, 0)).ToString();
        }

        // No assembly version at all is close to impossible, but answering with the informational
        // string minus its suffixes beats answering with nothing.
        var trimmed = informational?.Split('+')[0].Split('-')[0];
        return string.IsNullOrWhiteSpace(trimmed) ? UnknownVersion : trimmed;
    }

    /// <summary>
    /// The build metadata after <c>+</c>, when it looks like a revision. The SDK appends
    /// <c>SourceRevisionId</c> there, but the same field carries whatever else a build chooses to put
    /// in it, so anything that is not plausibly a hexadecimal commit id is reported as no commit
    /// rather than shown to an operator as one.
    /// </summary>
    private static string? CommitFrom(string? informational)
    {
        var plus = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (plus < 0)
        {
            return null;
        }

        var metadata = informational![(plus + 1)..];
        return metadata.Length is >= 7 and <= 40 && metadata.All(Uri.IsHexDigit) ? metadata : null;
    }

    private static DateTimeOffset? BuildDateFrom(string? stamped) =>
        DateTimeOffset.TryParse(
            stamped, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
