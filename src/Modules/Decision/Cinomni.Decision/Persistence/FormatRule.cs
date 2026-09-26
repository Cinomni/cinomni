namespace Cinomni.Decision.Persistence;

/// <summary>
/// A custom format: a named set of typed conditions plus the score it contributes when it matches
/// (the profile×format score is folded onto the rule for the single-profile MVP). <see cref="Negate"/>
/// inverts the overall match.
/// </summary>
public sealed class FormatRule
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    public required string Name { get; set; }

    public int Score { get; set; }

    public bool Negate { get; set; }

    public List<FormatCondition> Conditions { get; } = [];
}
