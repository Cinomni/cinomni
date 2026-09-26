using Cinomni.Decision.Contracts;

namespace Cinomni.Decision.Persistence;

/// <summary>
/// One append-only line of a decision's explanation: the rule, the property checked, the profile's
/// expectation vs the release's actual value, and the outcome.
/// </summary>
public sealed class DecisionReasonRecord
{
    public Guid EvaluationId { get; init; }

    public int Seq { get; init; }

    public required string Rule { get; init; }

    public string? Property { get; init; }

    public string? ProfileValue { get; init; }

    public string? ActualValue { get; init; }

    public ReasonOutcome Outcome { get; init; }

    public RejectionKind? Rejection { get; init; }
}
