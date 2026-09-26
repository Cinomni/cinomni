namespace Cinomni.Downloads.Engine;

/// <summary>
/// One observation of where the sidecar's traffic would actually go, as the engine reported it.
/// <para>
/// Three independent facts, kept apart rather than folded into a boolean, because they fail for
/// different reasons and the difference is what an operator needs: a tunnel that never came up is a
/// provider problem, a tunnel that is up but not routed is a race the tunnel process lost, and a
/// tunnel that holds the route while traffic still leaves with the household's address is a leak
/// neither of the first two would have caught.
/// </para>
/// </summary>
/// <param name="TunnelDevice">The interface the sidecar was told to bind to. Empty when none is configured.</param>
/// <param name="TunnelUp">The device exists and carries an address.</param>
/// <param name="DefaultRouteViaTunnel">The default route leaves through that device.</param>
/// <param name="EgressIdentityMatches">
/// The source address the kernel would put on the next outbound packet is the tunnel's own. This is
/// the leak check, and it is the one that cannot be inferred from configuration.
/// </param>
/// <param name="Reason">
/// A stable machine-readable cause, never a rendered sentence. Persisted and published, so it must
/// stay a vocabulary rather than become prose.
/// </param>
/// <param name="Policy">The loss policy the sidecar itself was configured with, as it reported it.</param>
/// <param name="SessionHeld">Whether the sidecar has already stopped its own torrent traffic.</param>
/// <param name="ObservedAt">When the observation was taken — not when it was asked for.</param>
public sealed record TunnelObservation(
    string TunnelDevice,
    bool TunnelUp,
    bool DefaultRouteViaTunnel,
    bool EgressIdentityMatches,
    string Reason,
    string Policy,
    bool SessionHeld,
    DateTimeOffset ObservedAt)
{
    /// <summary>Reported when the sidecar could not be reached or is too old to answer the question.</summary>
    public const string UnreachableReason = "sidecar-unreachable";

    /// <summary>Reported by the sidecar when all three facts hold.</summary>
    public const string VerifiedReason = "tunnel-egress-verified";

    /// <summary>
    /// True only when every fact holds. Anything else is a reason to stop, under a policy that says
    /// stopping is what to do.
    /// </summary>
    public bool Verified => TunnelUp && DefaultRouteViaTunnel && EgressIdentityMatches;

    /// <summary>
    /// The observation to use when the sidecar did not answer. Deliberately not "verified": an engine
    /// we cannot reach is not evidence that its traffic is safe, and treating silence as health is
    /// exactly the failure a kill-switch exists to prevent.
    /// </summary>
    public static TunnelObservation Unreachable(string device, DateTimeOffset now) =>
        new(device, false, false, false, UnreachableReason, string.Empty, false, now);
}
