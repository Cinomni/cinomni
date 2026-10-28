"""Tunnel egress guard: does this process's traffic actually leave through the tunnel?

The sidecar is the one process in Cinomni that talks to the swarm, so it is the one place where
"my traffic is leaving over the household's own connection" can be observed rather than assumed.
Three independent facts have to hold, and each of them fails on its own:

1. the tunnel device exists and carries an address — a tunnel that never came up;
2. the route the kernel would use for an ordinary packet leaves through that device. That is the
   lookup `ip route get` performs, policy rules included, not the default row of the main table. A
   WireGuard tunnel leaves the main default on the underlay so its own handshake can still leave, and
   installs the route everything else uses in another table. Reading the main table refuses that
   tunnel, and it can also miss a rule that sends traffic the other way. A tunnel that is up but not
   routed still fails here: a restarted tunnel process looks like this for the seconds before it
   reinstalls its routes;
3. the source address the kernel would put on an outbound connection is the tunnel's address. A route
   that names the tunnel device is not that fact: the kernel can still put the household's address on
   the packet, and a household address is a leak whether or not the route looks right.

Fact 3 is the leak detector, and it is deliberately **silent**: a UDP socket that is `connect()`ed
sends nothing (there is no handshake), but the kernel still performs the full route lookup and
`getsockname()` then reports the source address it picked. So the probe answers "which identity
would my next packet carry?" without emitting a packet — which matters, because a probe that had to
send one would itself be the leak it is looking for whenever the answer is bad.

The same question is asked a fourth time over IPv6, because a tunnel that carries IPv4 only leaves
native IPv6 egress untouched and libtorrent will announce and connect over it with the household's
own address. Every IPv4-only probe reports that arrangement as verified, which makes it the one leak
class a guard must not infer from configuration: if an IPv6 packet would leave with an address that
is not the tunnel's, the verdict is unverified. A namespace with no IPv6 egress at all — the ordinary
container — cannot leak this way and is not penalised for it.

What this module deliberately does NOT do is verify the packet filter. A silent probe cannot see a
firewall, and a probe that could see one would have to emit traffic to do it. The filter belongs to
the tunnel container whose namespace this process joins — it is that container's privilege, not
this one's, and this process runs with every capability dropped precisely so it cannot read or
change it. What is observed here is the outcome the filter is supposed to produce, which is the
part that matters: a filter that is loaded and a filter that works are not the same claim.

Everything above the probes is pure and takes its inputs as arguments, so the classification is
tested without a tunnel, without privileges and without a network.
"""

import os
import socket
import struct
import time

try:
    import fcntl
except ImportError:  # pragma: no cover - the sidecar only ever runs on Linux
    # The classification above is pure and has to stay importable off Linux so it can be unit-tested
    # on a contributor's machine. Only the ioctl probe needs the module, and it reports "no address"
    # without it, which is the same answer it gives for an absent device.
    fcntl = None

# Ordered from strongest to weakest. Anything below BLOCK is a conscious operator choice: it is
# logged by name at startup and reported on the health surface, never silently applied.
POLICY_BLOCK = "block"
POLICY_PAUSE_AND_ALERT = "pause-and-alert"
POLICY_IGNORE = "ignore"
POLICIES = (POLICY_BLOCK, POLICY_PAUSE_AND_ALERT, POLICY_IGNORE)

# Linux ioctl for "give me this interface's IPv4 address". The struct is the kernel's ifreq: a
# 16-byte interface name followed by a sockaddr_in whose address sits at offset 20.
_SIOCGIFADDR = 0x8915
_IFNAME_FIELD = 16
_IFADDR_OFFSET = 20
_IFADDR_LENGTH = 4

# A route query, not a route change. The numbers are the kernel's (`linux/rtnetlink.h`), and the
# message is host byte order, which is how netlink is defined. A GETROUTE with a destination and no
# table is the lookup `ip route get` performs.
_RTM_GETROUTE = 26
_RTM_NEWROUTE = 24
_NLM_F_REQUEST = 1
_NLMSG_ERROR = 2
_NLMSG_DONE = 3
_NLMSG_HDRLEN = 16
_RTMSG_SIZE = 12
_RTA_DST = 1
_RTA_OIF = 4

# Where the route probe pretends to be going. It is never contacted: `connect()` on a UDP socket
# performs the route lookup and returns, so nothing is sent to this address and nothing needs to
# exist there. It is a documentation address (RFC 5737 TEST-NET-3) precisely so that a future change
# that did start sending could not reach a real host by accident.
PROBE_DESTINATION = "203.0.113.1"

# The IPv6 counterpart, from the documentation prefix (RFC 3849), for the same reason.
PROBE_DESTINATION_V6 = "2001:db8::1"
PROBE_PORT = 9

# Where the kernel publishes the IPv6 addresses of every interface, as fixed columns of text.
INET6_TABLE = "/proc/net/if_inet6"

# The failure classes the probes report, so a caller can tell "no route at all" (which is what a
# correctly sealed box looks like) from "a route exists and it is not the tunnel's" (a leak).
REASON_OK = "tunnel-egress-verified"
REASON_NO_DEVICE = "tunnel-device-missing"
REASON_NO_ADDRESS = "tunnel-device-has-no-address"
REASON_ROUTE_ELSEWHERE = "default-route-not-via-tunnel"
REASON_IDENTITY_MISMATCH = "egress-identity-not-the-tunnel"
REASON_NO_EGRESS = "no-egress-route"
REASON_DISABLED = "tunnel-guard-disabled"
REASON_IPV6_LEAK = "ipv6-egress-not-the-tunnel"

# Reported when the observation itself could not be taken — a syscall that failed for a reason none
# of the probes expected. It is a failure to observe, never a verification: a guard that could not
# look has no grounds to say the traffic is safe.
REASON_OBSERVATION_FAILED = "tunnel-guard-observation-failed"


class Verdict:
    """One observation of the egress path, with the reason it was reached.

    The reason is carried rather than derived by the caller: it crosses the gRPC boundary into the
    backend, is persisted there and is what an operator is shown when downloads stop. Reducing it to
    a boolean would leave "downloads are held" with nothing behind it.
    """

    __slots__ = (
        "tunnel_device",
        "tunnel_up",
        "default_route_via_tunnel",
        "egress_identity_matches",
        "observed_source",
        "reason",
        "checked_at",
    )

    def __init__(
        self,
        tunnel_device="",
        tunnel_up=False,
        default_route_via_tunnel=False,
        egress_identity_matches=False,
        observed_source="",
        reason=REASON_NO_DEVICE,
        checked_at=0,
    ):
        self.tunnel_device = tunnel_device
        self.tunnel_up = tunnel_up
        self.default_route_via_tunnel = default_route_via_tunnel
        self.egress_identity_matches = egress_identity_matches
        self.observed_source = observed_source
        self.reason = reason
        self.checked_at = checked_at

    @property
    def healthy(self):
        """True only when all three independent facts hold. Any one of them failing is a leak risk."""
        return self.tunnel_up and self.default_route_via_tunnel and self.egress_identity_matches

    def __eq__(self, other):
        if not isinstance(other, Verdict):
            return NotImplemented
        return all(getattr(self, field) == getattr(other, field) for field in self.__slots__)

    def __repr__(self):
        return (
            f"Verdict(device={self.tunnel_device!r}, up={self.tunnel_up}, "
            f"route={self.default_route_via_tunnel}, identity={self.egress_identity_matches}, "
            f"reason={self.reason!r})"
        )


def normalize_policy(value):
    """The configured policy, or BLOCK when it is unset or not one of the three.

    An unrecognised value must not silently become the weakest mode, so it becomes the strongest one
    and the caller reports the substitution. Fail closed applies to configuration too.

    Hyphens and underscores are removed before matching, so this process and the backend read the
    same word out of the same variable whichever spelling the deployment used (`pause-and-alert` and
    `PauseAndAlert` are one setting). Two variables for one policy would let an operator configure
    the sidecar and the backend to enforce different rules.
    """
    candidate = (value or "").strip().lower().replace("-", "").replace("_", "")
    for policy in POLICIES:
        if candidate == policy.replace("-", ""):
            return policy

    return POLICY_BLOCK


def parse_default_route(route_table):
    """The interface of the main table's IPv4 default route, from `/proc/net/route`.

    This is not the route the guard decides on. That file is the main table only, and a WireGuard
    tunnel leaves its default there on the underlay while the route ordinary packets use lives in
    another table, selected by a policy rule. Deciding from this row refuses a healthy tunnel, and
    it cannot see a rule that sends traffic the other way. The guard asks the kernel instead
    (`route_lookup_device`). This parser stays because a dump of the main table is still a real
    observation, and the row that wins is the lowest metric, not the first row.

    The file is a fixed-column text table: a header line, then one row per route whose first field
    is the interface and whose second is the destination as a little-endian hex word. The default
    route is the row whose destination is all zeroes.
    """
    best_device = None
    best_metric = None

    for line in (route_table or "").splitlines()[1:]:
        fields = line.split()
        if len(fields) < 7 or fields[1].upper().strip("0") != "":
            continue

        try:
            metric = int(fields[6])
        except ValueError:
            continue

        if best_metric is None or metric < best_metric:
            best_device, best_metric = fields[0], metric

    return best_device


def _aligned(length):
    """Netlink's alignment: every header and attribute is padded up to a multiple of four."""
    return (length + 3) & ~3


def parse_route_lookup(message, destination=None):
    """The output interface index from an RTM_GETROUTE reply, or None.

    None is every failure: an error, a truncated message, an attribute that does not parse, and a
    reply that names no interface. A guard that could not read the lookup has not verified a route,
    and must not invent one from a partial message or from the source address sitting next to it.
    The main table's id, which a policy-routed reply still carries, is not an interface and is not
    read.

    When `destination` is given, the reply has to be the lookup for that address. A reply that names
    an interface and a different address, or that names an interface and no address, is not this
    process's question. Omitting it reads the index alone, which is what a test of the wire format
    needs and not what the guard decides from.
    """
    if not isinstance(message, (bytes, bytearray)) or not message:
        return None

    offset = 0
    while offset + _NLMSG_HDRLEN <= len(message):
        nlmsg_len, nlmsg_type = struct.unpack_from("IH", message, offset)
        if nlmsg_len < _NLMSG_HDRLEN or offset + nlmsg_len > len(message):
            return None
        if nlmsg_type in (_NLMSG_ERROR, _NLMSG_DONE):
            return None
        if nlmsg_type == _RTM_NEWROUTE:
            if nlmsg_len < _NLMSG_HDRLEN + _RTMSG_SIZE:
                return None
            return _accepted_oif(
                message,
                offset + _NLMSG_HDRLEN + _RTMSG_SIZE,
                offset + nlmsg_len,
                destination,
            )
        offset += _aligned(nlmsg_len)

    return None


def _accepted_oif(message, start, end, destination):
    """The output interface, if this attribute list is the lookup for `destination`.

    A truncated list is not a route, even when an interface index was already seen: the rest of the
    message is what says which address that index was for.
    """
    pos = start
    found = None
    replied = None
    while pos + 4 <= end:
        rta_len, rta_type = struct.unpack_from("HH", message, pos)
        if rta_len < 4 or pos + rta_len > end:
            return None
        if rta_type == _RTA_OIF:
            if rta_len < 8:
                return None
            found = struct.unpack_from("I", message, pos + 4)[0] or None
        elif rta_type == _RTA_DST and rta_len >= 8:
            replied = bytes(message[pos + 4 : pos + 8])
        pos += _aligned(rta_len)

    if destination is None:
        return found
    try:
        expected = socket.inet_aton(destination)
    except OSError:
        return None
    if replied != expected:
        return None
    return found


def classify(
    tunnel_device,
    device_present,
    device_address,
    default_route_device,
    observed_source,
    now,
    ipv6_leak=False,
):
    """Turn the observations into a verdict, in the order that makes the reason useful.

    Ordering matters for the explanation, not for the outcome: a missing device makes every later
    fact meaningless, so reporting "no default route" for a tunnel that never came up would send an
    operator looking in the wrong place.

    Presence is taken as its own observation rather than inferred from the absence of an address.
    They are different failures — a provider that never connected, against a tunnel process that came
    up and has not been given an address yet — and they are fixed in different places.

    `ipv6_leak` is the fourth fact and it folds into the third: an installation whose IPv6 packets
    would leave with an address the tunnel does not own has not verified its egress identity, however
    well the IPv4 half adds up. It defaults to False so a caller that cannot ask the question is not
    made to answer it.
    """
    if not tunnel_device:
        return Verdict(reason=REASON_DISABLED, checked_at=now)

    if not device_present:
        return Verdict(
            tunnel_device=tunnel_device,
            observed_source=observed_source or "",
            reason=REASON_NO_DEVICE,
            checked_at=now,
        )

    if not device_address:
        return Verdict(
            tunnel_device=tunnel_device,
            observed_source=observed_source or "",
            reason=REASON_NO_ADDRESS,
            checked_at=now,
        )

    route_matches = default_route_device == tunnel_device
    identity_matches = bool(observed_source) and observed_source == device_address and not ipv6_leak

    if not route_matches:
        reason = REASON_ROUTE_ELSEWHERE
    elif not observed_source:
        reason = REASON_NO_EGRESS
    elif observed_source != device_address:
        reason = REASON_IDENTITY_MISMATCH
    elif ipv6_leak:
        reason = REASON_IPV6_LEAK
    else:
        reason = REASON_OK

    return Verdict(
        tunnel_device=tunnel_device,
        tunnel_up=True,
        default_route_via_tunnel=route_matches,
        egress_identity_matches=identity_matches,
        observed_source=observed_source or "",
        reason=reason,
        checked_at=now,
    )


def observation_failed(tunnel_device, now=None):
    """The verdict for "the observation itself failed", stamped with the moment it failed.

    It is unhealthy by construction, and it carries a fresh timestamp on purpose: the alternative is
    leaving the previous verdict in place, which would let a guard that can no longer observe anything
    keep publishing the last good answer for as long as the process lives.
    """
    return Verdict(
        tunnel_device=tunnel_device or "",
        reason=REASON_OBSERVATION_FAILED,
        checked_at=int(time.time()) if now is None else now,
    )


# -- probes (impure; a failure to ask is "unknown", never a verification) --------------------------


def route_lookup_device(destination=PROBE_DESTINATION):
    """The interface an ordinary packet to `destination` would leave through.

    This is the kernel's own lookup, the one `ip route get` prints, policy rules included. Nothing
    is sent: the query is a netlink request to the local kernel, the same class of observation as
    the silent UDP connect. The reply counts only when it is for this destination. A lookup that
    cannot be taken — no netlink, no permission, no answer, an index that names nothing, a reply for
    another address — is None, which is not a route through the tunnel. It is never filled in from
    the main table, and never inferred from the source address the lookup also reports.
    """
    if not hasattr(socket, "AF_NETLINK") or not hasattr(socket, "NETLINK_ROUTE"):
        return None

    probe = None
    try:
        destination_bytes = socket.inet_aton(destination)
        attributes = struct.pack("HH", 4 + len(destination_bytes), _RTA_DST) + destination_bytes
        route = struct.pack("BBBBBBBBI", socket.AF_INET, 32, 0, 0, 0, 0, 0, 0, 0)
        payload = route + attributes
        header = struct.pack(
            "IHHII", _NLMSG_HDRLEN + len(payload), _RTM_GETROUTE, _NLM_F_REQUEST, 1, 0
        )
        probe = socket.socket(socket.AF_NETLINK, socket.SOCK_RAW, socket.NETLINK_ROUTE)
        probe.bind((0, 0))
        probe.settimeout(2)
        probe.send(header + payload)
        index = parse_route_lookup(probe.recv(8192), destination)
        if not index:
            return None
        return socket.if_indextoname(index)
    except (OSError, ValueError, AttributeError):
        return None
    finally:
        if probe is not None:
            probe.close()


def read_route_table(path="/proc/net/route"):
    """The main routing table as text, or an empty table when it cannot be read.

    Not the route the guard decides on — see `parse_default_route`. Kept so a caller that wants the
    main table can still read it without that failure becoming an exception.
    """
    try:
        with open(path, "r", encoding="ascii", errors="replace") as handle:
            return handle.read()
    except OSError:
        return ""


def device_present(device):
    """Whether the interface exists at all, by name.

    `if_nametoindex` asks the kernel for the interface's index and raises when there is none. It
    needs no privilege, sends nothing, and answers the one question an address lookup cannot: a
    tunnel that never came up and one that came up bare fail for different reasons and are fixed in
    different places, so they must not share a message.
    """
    if not device:
        return False

    try:
        return socket.if_nametoindex(device) > 0
    except (OSError, ValueError):
        return False


def device_address(device):
    """The IPv4 address bound to an interface, or None when the interface is absent or bare.

    The socket is created inside the `try`, not before it: this process is the file-descriptor-heavy
    one in the installation, and a `socket()` that raised outside the handler would escape a probe
    documented as never failing and take the guard thread with it.
    """
    if not device or fcntl is None:
        return None

    probe = None
    try:
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        request = struct.pack("256s", device.encode("ascii", "ignore")[: _IFNAME_FIELD - 1])
        answer = fcntl.ioctl(probe.fileno(), _SIOCGIFADDR, request)
        return socket.inet_ntoa(answer[_IFADDR_OFFSET : _IFADDR_OFFSET + _IFADDR_LENGTH])
    except (OSError, ValueError):
        return None
    finally:
        if probe is not None:
            probe.close()


def observed_source_address(destination=PROBE_DESTINATION, port=PROBE_PORT):
    """The source address the kernel would put on an outbound packet — without sending one.

    `connect()` on a UDP socket does not talk to anything: it records the peer and performs the
    route lookup, after which `getsockname()` reports the address that lookup selected. That is the
    identity the swarm would see. When no route exists at all the connect fails, which is reported
    as None and is what a properly sealed box looks like.
    """
    probe = None
    try:
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        probe.connect((destination, port))
        return probe.getsockname()[0]
    except OSError:
        return None
    finally:
        if probe is not None:
            probe.close()


def observed_source_address_v6(destination=PROBE_DESTINATION_V6, port=PROBE_PORT):
    """The IPv6 source address the kernel would use, by the same silent route lookup.

    None means this namespace has no IPv6 egress, which is the ordinary container and cannot leak
    over a family it does not have.
    """
    probe = None
    try:
        probe = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
        probe.connect((destination, port))
        return probe.getsockname()[0]
    except (OSError, ValueError):
        return None
    finally:
        if probe is not None:
            probe.close()


def read_inet6_table(path=INET6_TABLE):
    """The kernel's IPv6 interface table as text, or empty when it cannot be read."""
    try:
        with open(path, "r", encoding="ascii", errors="replace") as handle:
            return handle.read()
    except OSError:
        return ""


def parse_inet6_addresses(table, device):
    """Every IPv6 address bound to `device`, from `/proc/net/if_inet6`.

    The file is fixed columns with no header: the address as 32 hex characters, then the interface
    index, prefix length, scope and flags, and the device name last. The addresses are returned in
    the kernel's own compressed form so they compare equal to what `getsockname()` reports.
    """
    addresses = []
    if not device:
        return addresses

    for line in (table or "").splitlines():
        fields = line.split()
        if len(fields) < 6 or fields[5] != device or len(fields[0]) != 32:
            continue

        try:
            addresses.append(socket.inet_ntop(socket.AF_INET6, bytes.fromhex(fields[0])))
        except (ValueError, OSError):
            continue

    return addresses


def ipv6_egress_leaks(observed_source, device_addresses):
    """Whether an IPv6 packet would leave with an address the tunnel does not own.

    Three answers are not a leak and must not be treated as one: no IPv6 egress at all (the probe
    found no route), a link-local or loopback source (which reaches nothing off the box), and a
    source the tunnel device itself carries. Everything else is the household's own address on the
    swarm, which is exactly what the tunnel was chosen to prevent.
    """
    if not observed_source:
        return False

    candidate = observed_source.split("%", 1)[0].lower()
    if candidate in ("::1", "::") or candidate.startswith("fe80:"):
        return False

    return candidate not in [address.lower() for address in device_addresses]


def observe(tunnel_device, now=None):
    """One full observation: the device, the route the kernel would use, and the source address.

    The route is `route_lookup_device`, not the main table. Those two disagree on a healthy
    WireGuard tunnel, and the kernel's lookup is the one a packet would follow.
    """
    moment = int(time.time()) if now is None else now
    if not tunnel_device:
        return classify("", False, None, None, None, moment)

    return classify(
        tunnel_device,
        device_present(tunnel_device),
        device_address(tunnel_device),
        route_lookup_device(),
        observed_source_address(),
        moment,
        ipv6_leak=ipv6_egress_leaks(
            observed_source_address_v6(),
            parse_inet6_addresses(read_inet6_table(), tunnel_device),
        ),
    )


def configured_device():
    """The tunnel device this deployment binds to, or an empty string when the guard is off."""
    return (os.environ.get("SIDECAR_TUNNEL_DEVICE") or "").strip()


def configured_policy():
    """The configured loss policy, normalised. Unset or unrecognised means the strongest mode."""
    return normalize_policy(os.environ.get("SIDECAR_TUNNEL_POLICY"))
