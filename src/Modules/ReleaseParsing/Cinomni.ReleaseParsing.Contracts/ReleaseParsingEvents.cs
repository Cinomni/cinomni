using Cinomni.Kernel.Messaging;

namespace Cinomni.ReleaseParsing.Contracts;

/// <summary>Stable registered names of the Release Parsing integration events.</summary>
public static class ReleaseParsingEventNames
{
    public const string ReleaseParsed = "parsing.release-parsed";
}

/// <summary>
/// A release name was parsed and its audit record persisted. Carries the parser version so a
/// consumer can tell how the title was interpreted (explainability). Consumed by Decision
/// and kept for audit.
/// </summary>
public sealed record ReleaseParsed(Guid ReleaseId, string CanonicalKey, string ParserVersion) : DomainEvent
{
    public override string IdempotencyKey => $"release-parsed:{ReleaseId}";
}
