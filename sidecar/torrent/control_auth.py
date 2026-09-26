"""A credential on the sidecar's gRPC control port.

The control port is not a data surface: it adds torrents, redirects where they are written and
removes them with their files. Until now nothing authenticated it, which was defensible only while
it was reachable from one place — a private Compose network with no published port.

Joining a tunnel's network namespace ends that. In a shared namespace the port is bound inside a
network the operator does not own, and "whoever can reach the address" becomes "whatever else the
tunnel can route to". The address the port binds to is the first line of defence and it is not
enough on its own, because a bind address is a routing decision and routing is exactly what a VPN
provider changes underneath you.

**Why a shared token and not TLS.** A certificate proves *who* the peer is, and there is no second
identity here to distinguish: exactly one client exists, it is deployed in the same unit as the
server, and the channel is a loopback socket inside one namespace, so there is no path for a third
party to observe it. What is actually missing is proof that the caller is *the* client, which a
shared secret gives directly and which the deployment can rotate by restarting two containers. TLS
would add a certificate lifecycle — issuing, renewal, an expiry that silently stops every download
one morning — to buy confidentiality against an observer that cannot exist on this path. Should the
channel ever cross a host boundary, that trade reverses and this becomes the wrong answer; that is
recorded here so the decision is re-made rather than inherited.

The comparison is constant-time. The difference is measurable over a local socket, and an attacker
who can call the port can call it a great many times.
"""

import hmac
import os

# Lower-case on purpose: gRPC normalises metadata keys, and a key with capitals never matches.
TOKEN_METADATA_KEY = "x-cinomni-control-token"

# Short enough to be a typo and long enough to be a secret. A token below this is refused at startup
# rather than accepted and quietly guessed.
MIN_TOKEN_LENGTH = 32

UNAUTHENTICATED_MESSAGE = "missing or invalid control token"


def read_token(environment=None):
    """The configured control token, or an empty string when the port is deliberately open."""
    source = os.environ if environment is None else environment
    return (source.get("SIDECAR_CONTROL_TOKEN") or "").strip()


def token_required(environment=None):
    """Whether this deployment refuses to start without a token (the VPN overlay sets it)."""
    source = os.environ if environment is None else environment
    return (source.get("SIDECAR_REQUIRE_CONTROL_TOKEN") or "").strip() == "1"


def validate_configuration(token, required):
    """The startup rule, as a message to fail with — or None when the configuration is usable.

    Three outcomes, and the middle one is the point: a deployment that demands a token and has none
    must not start, because the alternative is a control port that is open in exactly the topology
    that made it reachable.
    """
    if token and len(token) < MIN_TOKEN_LENGTH:
        return (
            f"SIDECAR_CONTROL_TOKEN is shorter than {MIN_TOKEN_LENGTH} characters. "
            "Generate one with: openssl rand -hex 32"
        )

    if required and not token:
        return (
            "SIDECAR_REQUIRE_CONTROL_TOKEN is set but SIDECAR_CONTROL_TOKEN is empty. "
            "The control port would accept anything that can reach it."
        )

    return None


def presented_token(metadata):
    """The token a caller presented, from gRPC's key/value metadata pairs. Empty when absent."""
    for key, value in metadata or ():
        if key == TOKEN_METADATA_KEY:
            return value or ""

    return ""


def _as_bytes(value):
    """A metadata value as bytes, whatever the caller put on the wire.

    `hmac.compare_digest` raises `TypeError` for a `str` holding any non-ASCII character rather than
    returning False, and a caller that speaks HTTP/2 directly can present exactly that. Comparing
    bytes keeps the answer a refusal instead of an unhandled exception the interceptor would turn
    into an UNKNOWN status — which is noise a caller can generate cheaply and repeatedly.
    """
    if isinstance(value, (bytes, bytearray)):
        return bytes(value)

    return str(value).encode("utf-8", "surrogateescape")


def is_authorized(metadata, expected):
    """Whether a call may proceed.

    An empty expected token means the port is open by configuration — that is a deployment choice,
    reported at startup, not a check that failed. Otherwise the presented token must match exactly,
    compared in constant time.
    """
    if not expected:
        return True

    try:
        return hmac.compare_digest(_as_bytes(presented_token(metadata)), _as_bytes(expected))
    except (TypeError, ValueError, UnicodeError):
        return False


def build_interceptor(expected):
    """A gRPC server interceptor enforcing `expected`, or None when the port is configured open.

    `grpc` is imported here rather than at module scope so the rules above stay unit-testable in a
    plain interpreter, with no gRPC runtime and no generated stubs.
    """
    if not expected:
        return None

    import grpc  # noqa: PLC0415 - deliberate: keeps the pure rules importable without gRPC.

    # A unary handler is the right refusal for every method on this service because every one of them
    # takes a single request message; the server reads that one message, calls this, and the abort
    # ends the call before any generated handler is reached. A client-streaming method added later
    # would need a matching handler, which is why the proto is worth checking before adding one.
    denial = grpc.unary_unary_rpc_method_handler(
        lambda request, context: context.abort(grpc.StatusCode.UNAUTHENTICATED, UNAUTHENTICATED_MESSAGE)
    )

    class _TokenInterceptor(grpc.ServerInterceptor):
        """Refuses every call that does not carry the token, before the handler is ever resolved."""

        def intercept_service(self, continuation, handler_call_details):
            if is_authorized(handler_call_details.invocation_metadata, expected):
                return continuation(handler_call_details)

            return denial

    return _TokenInterceptor()
