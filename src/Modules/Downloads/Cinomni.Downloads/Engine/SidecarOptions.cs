using Cinomni.Downloads.Contracts;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// Configuration for reaching the libtorrent sidecar. In production the sidecar lives in the VPN
/// network namespace; here it defaults to the local h2c endpoint the PoC used.
/// </summary>
public sealed class SidecarOptions
{
    /// <summary>gRPC endpoint of the sidecar (cleartext h2c on localhost by default).</summary>
    public string Address { get; set; } = "http://localhost:50051";

    /// <summary>Staging directory the sidecar downloads into (the Import boundary).</summary>
    public string StagingPath { get; set; } = "/data/downloads";

    /// <summary>
    /// The shared secret presented on every call to the control port. Empty means the port is open,
    /// which is the development default and is what the sidecar warns about at startup.
    /// <para>
    /// It exists because the control port adds torrents, decides where they are written and removes
    /// them with their files, and because joining a tunnel's network namespace puts that port inside
    /// a network the operator does not own. Never logged, never echoed in an error.
    /// </para>
    /// </summary>
    public string ControlToken { get; set; } = string.Empty;

    /// <summary>
    /// Deadline on every call to the sidecar. Without one a dead engine turns a queued command into a
    /// handler that never returns, which is worse than a failure: the command is neither done nor
    /// retryable, and the worker slot is gone.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The opt-in tunnel guard. Everything here is inert until <see cref="Device"/> names an interface:
/// with no tunnel the watch job is never scheduled, the sidecar is never asked about its egress, and
/// Cinomni behaves exactly as it does today. Local development needs no tunnel and gets none.
/// </summary>
public sealed class TunnelOptions
{
    /// <summary>
    /// The interface the sidecar binds its torrent traffic to (for example <c>tun0</c>). Empty — the
    /// default — means no tunnel is configured and the whole guard is off.
    /// </summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// What to do when egress can no longer be verified. <see cref="TunnelLossPolicy.Block"/> unless
    /// an operator says otherwise, and an unrecognised value is treated as <see cref="TunnelLossPolicy.Block"/>
    /// rather than accepted: a typo must not be able to weaken an installation.
    /// </summary>
    public TunnelLossPolicy LossPolicy { get; set; } = TunnelLossPolicy.Block;

    /// <summary>How often the watch job re-observes. Becomes a scheduled job, so it is read once.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many consecutive failed observations hold the downloads. Two by default: the sidecar has
    /// already stopped its own traffic on the first one, so this cadence decides when the *durable*
    /// decision is taken, and taking it on a single blip would pause a household's library for a
    /// hiccup that lasted one poll.
    /// </summary>
    public int UnverifiedThreshold { get; set; } = 2;

    /// <summary>
    /// How many consecutive verified observations release them again. Asymmetry is deliberate: it is
    /// cheap to stay stopped a little too long and expensive to resume a little too early.
    /// </summary>
    public int VerifiedThreshold { get; set; } = 2;

    /// <summary>Whether a tunnel is configured at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Device);

    /// <summary>
    /// Whether this installation asked the guard to <b>act</b> on what it observes.
    /// <see cref="TunnelLossPolicy.Ignore"/> is an explicit opt-out: the operator asked to be able to
    /// see the verdict and asked Cinomni not to stop anything over it, so nothing here may refuse on
    /// its behalf.
    /// </summary>
    public bool GuardActs => IsConfigured && LossPolicy is not TunnelLossPolicy.Ignore;

    /// <summary>
    /// How old an observation may be and still stand for the present. Three polls of slack absorbs a
    /// missed cycle and a slow one; the two-minute floor keeps a short poll interval from turning
    /// ordinary scheduling jitter into an outage.
    /// <para>
    /// Read in two places, for the same reason in both. The watch applies it to the sidecar's answer,
    /// because a guard thread that stopped observing keeps publishing its last verdict. Startup
    /// recovery applies it to the persisted verdict, because the row it reads was written by a process
    /// that no longer exists — and "the tunnel was fine when we died" is not evidence that it is fine
    /// now.
    /// </para>
    /// </summary>
    public TimeSpan MaxObservationAge =>
        TimeSpan.FromTicks(Math.Max(PollInterval.Ticks * 3, TimeSpan.FromMinutes(2).Ticks));

    /// <exception cref="InvalidOperationException">The configured values are unusable.</exception>
    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Downloads:Tunnel:{nameof(PollInterval)} must be a positive duration (configured: {PollInterval}).");
        }

        if (UnverifiedThreshold < 1)
        {
            throw new InvalidOperationException(
                $"Downloads:Tunnel:{nameof(UnverifiedThreshold)} must be at least 1 "
                + $"(configured: {UnverifiedThreshold}).");
        }

        if (VerifiedThreshold < 1)
        {
            throw new InvalidOperationException(
                $"Downloads:Tunnel:{nameof(VerifiedThreshold)} must be at least 1 "
                + $"(configured: {VerifiedThreshold}).");
        }

        if (!Enum.IsDefined(LossPolicy))
        {
            throw new InvalidOperationException(
                $"Downloads:Tunnel:{nameof(LossPolicy)} must be Block, PauseAndAlert or Ignore "
                + $"(configured: {(int)LossPolicy}).");
        }

        // A device named but never bound would be the worst of both: the guard reports on an
        // interface the sidecar is not using, so it would verify an egress path nothing takes.
        if (Device.Length > 0 && Device.Trim().Length != Device.Length)
        {
            throw new InvalidOperationException(
                $"Downloads:Tunnel:{nameof(Device)} must not have leading or trailing whitespace.");
        }
    }
}
