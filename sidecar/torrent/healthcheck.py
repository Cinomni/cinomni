"""Container health probe for the torrent sidecar.

Two questions, and the second one only exists when a tunnel is configured.

**Is the process serving?** The sidecar has no HTTP surface and the image carries no HTTP client,
so the cheapest honest signal is that the gRPC control port is accepting connections at the address
the server was told to bind. The connection is made from inside the container, so it opens nothing.

**Is its traffic leaving through the tunnel?** Under the `block` policy that is not a detail, it is
the contract: a container whose tunnel has gone is a container that must not be handed work, and
saying so here is what lets the orchestrator act on it — restart it, stop routing to it — without
waiting for the backend to notice. The observation is the same silent one the server makes and
costs three syscalls, so it is cheap enough to run on the health interval.

The probe deliberately does not call the gRPC surface for this: it now needs a credential, and a
health probe that carries the control token would put that token in the container's process list.

Exit code 0 means healthy; anything else means unhealthy.
"""

import os
import socket
import sys

import netguard

CONNECT_TIMEOUT_SECONDS = 3
DEFAULT_LISTEN = "[::]:50051"
# The spellings that mean "every interface". Only for those is loopback a valid place to knock.
WILDCARD_HOSTS = ("", "*", "0.0.0.0", "::")
LOOPBACK_ADDRESSES = ("127.0.0.1", "::1")


def split_listen(listen: str) -> tuple[str, int]:
    """The host and port of SIDECAR_LISTEN, which is a host:port pair with an optional bracketed host."""
    host, _, port = listen.rpartition(":")
    return host.strip("[]"), int(port)


def probe_addresses(host: str) -> tuple[str, ...]:
    """Where to knock: a server bound to one address answers there, a wildcard answers on loopback."""
    return LOOPBACK_ADDRESSES if host in WILDCARD_HOSTS else (host,)


def tunnel_failure() -> str:
    """Why the tunnel contract is not met, or an empty string when it is (or does not apply).

    Only `block` makes an unverified tunnel unhealthy. Under the two weaker policies the operator
    has chosen to be told rather than stopped, and reporting the container unhealthy would restart
    it forever instead — which is the same as stopping it, taken out of their hands.
    """
    device = netguard.configured_device()
    if not device or netguard.configured_policy() != netguard.POLICY_BLOCK:
        return ""

    try:
        verdict = netguard.observe(device)
    except Exception as error:  # noqa: BLE001 - a probe that could not be taken is not a verification
        return f"{netguard.REASON_OBSERVATION_FAILED} ({type(error).__name__})"

    return "" if verdict.healthy else verdict.reason


def main() -> int:
    listen = os.environ.get("SIDECAR_LISTEN", DEFAULT_LISTEN)

    try:
        host, port = split_listen(listen)
    except ValueError:
        print("SIDECAR_LISTEN does not end in a port number", file=sys.stderr)
        return 1

    # Which family reaches a wildcard bind depends on how the kernel is configured, so try both.
    listening = False
    for address in probe_addresses(host):
        try:
            socket.create_connection((address, port), CONNECT_TIMEOUT_SECONDS).close()
            listening = True
            break
        except OSError:
            continue

    if not listening:
        print(f"nothing is listening for {listen}", file=sys.stderr)
        return 1

    failure = tunnel_failure()
    if failure:
        print(f"tunnel egress not verified: {failure}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
