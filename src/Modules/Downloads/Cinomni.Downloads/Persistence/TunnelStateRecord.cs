using Cinomni.Downloads.Contracts;

namespace Cinomni.Downloads.Persistence;

/// <summary>
/// The installation's egress state, as one row in the <c>downloads</c> schema.
/// <para>
/// It exists because every part of this decision has to survive a restart. The hysteresis counters
/// decide when a flap becomes an outage, and a counter kept in memory would reset each time the
/// process restarted — which, during a bad tunnel, is exactly when it restarts. The transition
/// sequence is what makes the loss and restoration events idempotent across redelivery. And the last
/// reason is what an operator who arrives an hour later is shown instead of a silent pause.
/// </para>
/// <para>
/// Singleton by construction: <see cref="SingletonId"/> is the only key, so there can never be two
/// histories of the same fact. It is not a lock: two overlapping cycles both read this row before
/// either writes, and what keeps that from becoming two alerts is the transition sequence in the
/// downstream idempotency key, not the row itself. The watch is a scheduled, claimed job on a single
/// node, so overlapping runs are the unusual case rather than the designed-for one.
/// </para>
/// </summary>
public sealed class TunnelStateRecord
{
    private const int ReasonMaxLength = 200;
    private const int DeviceMaxLength = 64;

    /// <summary>
    /// The one row's identity. A fixed value rather than a generated one: there is exactly one egress
    /// path per installation, and a table that could hold two would eventually hold two.
    /// </summary>
    public static readonly Guid SingletonId = new("0198f7a0-0000-7000-8000-000000000001");

    public Guid Id { get; init; } = SingletonId;

    /// <summary>
    /// Whether the last observation found traffic verifiably inside the tunnel. False until one has
    /// been taken: "nothing has been checked yet" must never render as a verification, and
    /// <see cref="TunnelEgressStatus.Configured"/> is what tells an operator whether that even
    /// applies to their installation.
    /// </summary>
    public bool Verified { get; private set; }

    /// <summary>The machine-readable cause of the last observation. Never a rendered sentence.</summary>
    public string Reason { get; private set; } = TunnelObservationReasons.NotYetObserved;

    /// <summary>The interface the sidecar reported binding to, for the operator's benefit.</summary>
    public string TunnelDevice { get; private set; } = string.Empty;

    /// <summary>Consecutive observations that failed to verify. Reset by a verified one.</summary>
    public int UnverifiedStreak { get; private set; }

    /// <summary>Consecutive observations that verified. Reset by a failed one.</summary>
    public int VerifiedStreak { get; private set; }

    /// <summary>
    /// How many losses this installation has declared. Names the loss/restoration event pair, so a
    /// redelivered transition deduplicates and a second outage does not.
    /// </summary>
    public int TransitionSequence { get; private set; }

    /// <summary>Whether downloads are currently held. The durable half of the decision.</summary>
    public bool Holding { get; private set; }

    /// <summary>When the last observation was taken.</summary>
    public DateTimeOffset? ObservedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The starting state: nothing observed yet, nothing held, no transitions declared.</summary>
    public static TunnelStateRecord Initial(DateTimeOffset now) => new()
    {
        Id = SingletonId,
        Reason = TunnelObservationReasons.NotYetObserved,
        UpdatedAt = now,
    };

    /// <summary>
    /// Folds one observation into the streaks and records what was seen. Returns nothing: whether the
    /// streak has crossed a threshold is the caller's decision, because only the caller knows the
    /// configured policy.
    /// </summary>
    public void Observe(bool verified, string reason, string device, DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (verified)
        {
            VerifiedStreak += 1;
            UnverifiedStreak = 0;
        }
        else
        {
            UnverifiedStreak += 1;
            VerifiedStreak = 0;
        }

        Verified = verified;
        Reason = Truncate(reason, ReasonMaxLength);
        TunnelDevice = Truncate(device, DeviceMaxLength);
        ObservedAt = observedAt;
        UpdatedAt = now;
    }

    /// <summary>
    /// Declares a loss and returns the sequence it was given, or null when one is already declared.
    /// The null is what makes a redelivered or re-run watch a no-op rather than a second outage.
    /// </summary>
    public int? BeginHold(DateTimeOffset now)
    {
        if (Holding)
        {
            return null;
        }

        Holding = true;
        TransitionSequence += 1;
        UpdatedAt = now;
        return TransitionSequence;
    }

    /// <summary>Declares the loss over and returns its sequence, or null when nothing was held.</summary>
    public int? EndHold(DateTimeOffset now)
    {
        if (!Holding)
        {
            return null;
        }

        Holding = false;
        UpdatedAt = now;
        return TransitionSequence;
    }

    /// <summary>Projects the persisted state into the published read model.</summary>
    public TunnelEgressStatus ToStatus(bool configured, TunnelLossPolicy policy, int heldTaskCount) => new(
        configured,
        policy,
        Verified,
        Reason,
        TunnelDevice,
        heldTaskCount,
        TransitionSequence,
        ObservedAt);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}

/// <summary>
/// The vocabulary of egress reasons this module originates, beside the ones the sidecar sends
/// (<c>tunnel-egress-verified</c>, <c>tunnel-device-missing</c>, <c>tunnel-device-has-no-address</c>,
/// <c>default-route-not-via-tunnel</c>, <c>egress-identity-not-the-tunnel</c>,
/// <c>ipv6-egress-not-the-tunnel</c>, <c>no-egress-route</c>, <c>tunnel-guard-disabled</c>,
/// <c>tunnel-guard-observation-failed</c>) and <see cref="TunnelObservation.UnreachableReason"/>.
/// <para>
/// One list, in one place, because the value is persisted, published on the outbox, put in the
/// operator's notification and translated by the interface: a reason that exists in only two of the
/// four is shown raw to the person it was written for.
/// </para>
/// </summary>
public static class TunnelObservationReasons
{
    /// <summary>No observation has been taken yet — a fresh installation, or one that just started.</summary>
    public const string NotYetObserved = "not-yet-observed";

    /// <summary>
    /// The sidecar answered, but with an observation too old to stand for the present. A guard thread
    /// that stopped observing keeps publishing its last verdict, so age is the only thing that
    /// distinguishes a fresh verification from a recording of one.
    /// </summary>
    public const string ObservationStale = "tunnel-observation-stale";

    /// <summary>
    /// The sidecar reported enforcing a weaker loss policy than this installation configured. The two
    /// halves of the kill-switch are configured separately, and a sidecar on <c>ignore</c> never stops
    /// its own traffic however hard the backend holds.
    /// </summary>
    public const string PolicyDivergent = "tunnel-policy-divergent";

    /// <summary>The sidecar reported binding to a different interface than the one configured here.</summary>
    public const string DeviceDivergent = "tunnel-device-divergent";

    /// <summary>
    /// The tunnel was removed from the configuration while downloads were held. Recorded rather than
    /// blanked, because "why did these downloads stop and then start again" has an answer.
    /// </summary>
    public const string GuardRemoved = "tunnel-guard-removed";
}
