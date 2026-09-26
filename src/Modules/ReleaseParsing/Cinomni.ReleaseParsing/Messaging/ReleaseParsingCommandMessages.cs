using Cinomni.Kernel.Messaging;

namespace Cinomni.ReleaseParsing.Messaging;

/// <summary>Stable registered names of the Release Parsing commands.</summary>
public static class ReleaseParsingCommandNames
{
    public const string ParseAndPersist = "parsing.parse-and-persist";
    public const string PurgeParsedReleases = "parsing.purge-parsed-releases";
}

/// <summary>
/// Ages the parse audit trail out. Parameterless — the scheduler constructs it and the window is
/// deployment configuration.
/// </summary>
public sealed record PurgeParsedReleasesCommand : ICommand;

/// <summary>
/// Parses a release title and persists the audit record, emitting <c>ReleaseParsed</c>. The only
/// effectful operation of an otherwise pure module — a caller (Decision) enqueues it when it wants
/// the durable audit trail; the pure <c>IReleaseParser</c> is used directly when it does not.
/// </summary>
public sealed record ParseAndPersistCommand(string ReleaseTitle) : ICommand;
