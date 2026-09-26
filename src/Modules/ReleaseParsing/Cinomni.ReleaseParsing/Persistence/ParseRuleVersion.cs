namespace Cinomni.ReleaseParsing.Persistence;

/// <summary>
/// A version of the parser's rule set. Recorded so a re-parse batch can be triggered when the
/// rules change, and so a persisted <c>parser_version</c> is traceable to its ruleset.
/// </summary>
public sealed class ParseRuleVersion
{
    public required string Version { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
