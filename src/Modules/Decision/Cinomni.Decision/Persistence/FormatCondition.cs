using Cinomni.Decision.Contracts;

namespace Cinomni.Decision.Persistence;

/// <summary>
/// One typed condition of a custom format. Matching is OR within a type and AND across types;
/// <see cref="Negate"/> inverts a single test and <see cref="Required"/> makes it mandatory.
/// </summary>
public sealed class FormatCondition
{
    public Guid Id { get; init; }

    public Guid RuleId { get; init; }

    public FormatConditionType Type { get; init; }

    public required string Value { get; init; }

    public bool Negate { get; init; }

    public bool Required { get; init; }
}
