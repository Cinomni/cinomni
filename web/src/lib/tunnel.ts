import type { TunnelEgressStatus, TunnelLossPolicy } from '@/api/types'

/**
 * Every reason the tunnel guard can report, as a sentence an operator can act on.
 *
 * The API promises this vocabulary is machine-readable and never rendered raw, so the whole of it is
 * listed here rather than the common half: a value missing from this map would reach an operator as
 * the machine name they were told they would not see. The fallback below exists for a backend that
 * added a reason before this client learned it, and it says so honestly instead of inventing a
 * diagnosis.
 */
const REASONS: Record<string, string> = {
  // Observed by the sidecar, inside the network namespace it shares with the tunnel.
  'tunnel-egress-verified': 'Torrent traffic leaves through the tunnel.',
  'tunnel-device-missing': 'The tunnel device is not present. The tunnel container is probably down.',
  'tunnel-device-has-no-address': 'The tunnel device exists but has no address, so it cannot carry traffic yet.',
  'default-route-not-via-tunnel': 'The default route no longer points at the tunnel, so traffic would leave unprotected.',
  'egress-identity-not-the-tunnel': 'Traffic is leaving under an address that is not the tunnel’s.',
  'ipv6-egress-not-the-tunnel': 'IPv6 traffic is leaving outside the tunnel, even though IPv4 is inside it.',
  'no-egress-route': 'There is no route out at all.',
  'tunnel-guard-disabled': 'The guard is switched off in the sidecar, so nothing is checking egress.',
  'tunnel-guard-observation-failed': 'The guard could not complete its check, so egress is unverified.',

  // Concluded here, from what the sidecar did or did not say.
  'sidecar-unreachable': 'The sidecar cannot be reached, so egress cannot be verified.',
  'not-yet-observed': 'The guard has not reported since this process started.',
  'tunnel-observation-stale': 'The last observation is too old to stand for the present.',
  'tunnel-policy-divergent': 'The sidecar is enforcing a weaker policy than this installation configured.',
  'tunnel-device-divergent': 'The sidecar is watching a different interface than this installation configured.',
  'tunnel-guard-removed': 'The guard is no longer running in the sidecar.',
}

export function tunnelReasonSentence(reason: string): string {
  return REASONS[reason] ?? 'The guard reported a state this interface does not recognise. Check the server logs.'
}

/** What each loss policy actually does to a transfer, in the operator's terms. */
export const tunnelPolicyLabel: Record<TunnelLossPolicy, string> = {
  Block: 'Stop transfers (fail closed)',
  PauseAndAlert: 'Pause transfers and alert',
  Ignore: 'Keep transferring',
}

export const tunnelPolicyExplanation: Record<TunnelLossPolicy, string> = {
  Block: 'Losing the tunnel stops every transfer at the engine until egress is verified again.',
  PauseAndAlert: 'Losing the tunnel pauses transfers and raises a notification. An operator can override a hold.',
  Ignore: 'Transfers continue whatever the tunnel does. This installation accepts unprotected traffic.',
}

/**
 * Whether a status is worth interrupting an operator over.
 *
 * An unconfigured tunnel is the ordinary, supported state and reads as neutral, not as a warning:
 * the VPN is opt-in, and an installation without one is not misconfigured. Silence, though, is never
 * health — a configured tunnel that is merely unverified is a warning, because unverified means
 * exactly that and not "fine".
 */
export function tunnelTone(status: TunnelEgressStatus): 'neutral' | 'success' | 'warning' {
  if (!status.configured) return 'neutral'
  return status.verified ? 'success' : 'warning'
}
