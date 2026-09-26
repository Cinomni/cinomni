"""Small fail-closed forward proxy for the isolated browser container."""

from __future__ import annotations

import argparse
import ipaddress
import os
import random
import re
import selectors
import socket
import socketserver
import struct
import sys
import threading
import time
from dataclasses import dataclass
from urllib.parse import urlsplit

LISTEN_HOST = os.environ.get("PROXY_LISTEN_HOST", "0.0.0.0")
LISTEN_PORT = int(os.environ.get("PROXY_LISTEN_PORT", "8080"))
CONNECT_TIMEOUT = 10.0
DNS_TIMEOUT = 5.0
IDLE_TIMEOUT = 20.0
MAX_LIFETIME = 120.0
MAX_HEADER_BYTES = 32 * 1024
MAX_HEADER_LINE = 8 * 1024
MAX_HEADERS = 100
MAX_BODY_BYTES = 16 * 1024 * 1024
MAX_RELAY_BYTES = 32 * 1024 * 1024
MAX_CONCURRENCY = 32
METHOD_RE = re.compile(rb"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$")
HEADER_NAME_RE = METHOD_RE
METADATA_ADDRESSES = {
    ipaddress.ip_address("169.254.169.254"),
    ipaddress.ip_address("fd00:ec2::254"),
}


class ProxyError(Exception):
    def __init__(self, status: int, reason: str):
        super().__init__(reason)
        self.status = status
        self.reason = reason


@dataclass(frozen=True)
class Request:
    method: str
    host: str
    port: int
    path: str | None
    version: str
    headers: tuple[tuple[str, str], ...]
    content_length: int


def is_public_destination(value: str | ipaddress.IPv4Address | ipaddress.IPv6Address) -> bool:
    address = ipaddress.ip_address(value)
    if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped:
        address = address.ipv4_mapped
    return (
        address not in METADATA_ADDRESSES
        and address.is_global
        and not address.is_loopback
        and not address.is_private
        and not address.is_link_local
        and not address.is_multicast
        and not address.is_reserved
        and not address.is_unspecified
    )


def _parse_authority(authority: str, default_port: int | None) -> tuple[str, int]:
    if not authority or "@" in authority or any(character.isspace() for character in authority):
        raise ProxyError(400, "invalid authority")
    if authority.startswith("["):
        closing = authority.find("]")
        if closing < 0 or "%" in authority[:closing]:
            raise ProxyError(400, "invalid authority")
        host = authority[1:closing]
        suffix = authority[closing + 1 :]
        if suffix:
            if not suffix.startswith(":"):
                raise ProxyError(400, "invalid authority")
            port_text = suffix[1:]
        else:
            port_text = ""
    else:
        if authority.count(":") > 1:
            raise ProxyError(400, "IPv6 authority must use brackets")
        host, separator, port_text = authority.rpartition(":")
        if not separator:
            host, port_text = authority, ""
    if not host or "%" in host:
        raise ProxyError(400, "invalid authority")
    if port_text:
        if not port_text.isascii() or not port_text.isdigit():
            raise ProxyError(400, "invalid port")
        port = int(port_text)
    elif default_port is not None:
        port = default_port
    else:
        raise ProxyError(400, "port is required")
    if not 1 <= port <= 65535:
        raise ProxyError(400, "invalid port")
    try:
        host = host.encode("idna").decode("ascii").lower().rstrip(".")
    except UnicodeError as error:
        raise ProxyError(400, "invalid host") from error
    if not host or len(host) > 253:
        raise ProxyError(400, "invalid host")
    return host, port


def parse_request(header_block: bytes) -> Request:
    if len(header_block) > MAX_HEADER_BYTES or not header_block.endswith(b"\r\n\r\n"):
        raise ProxyError(431, "headers too large or incomplete")
    lines = header_block[:-4].split(b"\r\n")
    if not lines or len(lines[0]) > MAX_HEADER_LINE:
        raise ProxyError(400, "invalid request line")
    parts = lines[0].split(b" ")
    if len(parts) != 3 or not METHOD_RE.fullmatch(parts[0]):
        raise ProxyError(400, "invalid request line")
    try:
        method = parts[0].decode("ascii").upper()
        target = parts[1].decode("ascii")
        version = parts[2].decode("ascii")
    except UnicodeDecodeError as error:
        raise ProxyError(400, "request line must be ASCII") from error
    if version not in ("HTTP/1.0", "HTTP/1.1"):
        raise ProxyError(505, "HTTP version not supported")

    if len(lines) - 1 > MAX_HEADERS:
        raise ProxyError(431, "too many headers")
    headers: list[tuple[str, str]] = []
    seen: dict[str, list[str]] = {}
    for line in lines[1:]:
        if not line or len(line) > MAX_HEADER_LINE or line[:1] in (b" ", b"\t"):
            raise ProxyError(400, "invalid header")
        name, separator, raw_value = line.partition(b":")
        if not separator or not HEADER_NAME_RE.fullmatch(name):
            raise ProxyError(400, "invalid header")
        try:
            decoded_name = name.decode("ascii").lower()
            value = raw_value.strip(b" \t").decode("latin-1")
        except UnicodeDecodeError as error:
            raise ProxyError(400, "invalid header") from error
        if "\r" in value or "\n" in value or "\x00" in value:
            raise ProxyError(400, "invalid header")
        headers.append((decoded_name, value))
        seen.setdefault(decoded_name, []).append(value)

    if "transfer-encoding" in seen:
        raise ProxyError(400, "transfer encoding is not accepted")
    lengths = seen.get("content-length", [])
    if len(lengths) > 1 or (lengths and (not lengths[0].isdigit())):
        raise ProxyError(400, "invalid content length")
    content_length = int(lengths[0]) if lengths else 0
    if content_length > MAX_BODY_BYTES:
        raise ProxyError(413, "request body too large")

    if method == "CONNECT":
        host, port = _parse_authority(target, None)
        path = None
    else:
        parsed = urlsplit(target)
        if parsed.scheme.lower() != "http" or not parsed.netloc or parsed.fragment:
            raise ProxyError(400, "absolute http target required")
        if parsed.username is not None or parsed.password is not None:
            raise ProxyError(400, "userinfo is not accepted")
        try:
            parsed_port = parsed.port
        except ValueError as error:
            raise ProxyError(400, "invalid port") from error
        host, port = _parse_authority(
            f"[{parsed.hostname}]:{parsed_port}" if parsed.hostname and ":" in parsed.hostname and parsed_port else
            f"[{parsed.hostname}]" if parsed.hostname and ":" in parsed.hostname else
            f"{parsed.hostname}:{parsed_port}" if parsed_port else str(parsed.hostname or ""),
            80,
        )
        path = parsed.path or "/"
        if parsed.query:
            path += "?" + parsed.query

    if len(seen.get("host", [])) > 1:
        raise ProxyError(400, "multiple host headers")
    return Request(method, host, port, path, version, tuple(headers), content_length)


def _read_nameservers() -> tuple[str, ...]:
    servers: list[str] = []
    try:
        with open("/etc/resolv.conf", encoding="ascii") as resolv:
            for line in resolv:
                fields = line.split()
                if len(fields) >= 2 and fields[0] == "nameserver":
                    ipaddress.ip_address(fields[1])
                    servers.append(fields[1])
    except (OSError, UnicodeError, ValueError):
        pass
    if not servers:
        raise ProxyError(502, "no DNS resolver available")
    return tuple(servers[:3])


def _dns_name(name: str) -> bytes:
    labels = name.rstrip(".").split(".")
    if any(not label or len(label.encode("ascii")) > 63 for label in labels):
        raise ProxyError(400, "invalid DNS name")
    return b"".join(bytes((len(label),)) + label.encode("ascii") for label in labels) + b"\0"


def _skip_dns_name(packet: bytes, offset: int) -> int:
    visited = 0
    while True:
        if offset >= len(packet) or visited > 128:
            raise ProxyError(502, "malformed DNS response")
        length = packet[offset]
        if length & 0xC0 == 0xC0:
            if offset + 1 >= len(packet):
                raise ProxyError(502, "malformed DNS response")
            return offset + 2
        if length & 0xC0 or length > 63:
            raise ProxyError(502, "malformed DNS response")
        offset += 1
        if length == 0:
            return offset
        offset += length
        visited += 1


def _dns_exchange(server: str, query: bytes, deadline: float) -> bytes:
    family = socket.AF_INET6 if ":" in server else socket.AF_INET
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ProxyError(504, "DNS timeout")
    with socket.socket(family, socket.SOCK_DGRAM) as udp:
        udp.settimeout(remaining)
        udp.connect((server, 53))
        udp.send(query)
        response = udp.recv(4096)
    if len(response) >= 4 and struct.unpack("!H", response[2:4])[0] & 0x0200:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ProxyError(504, "DNS timeout")
        with socket.socket(family, socket.SOCK_STREAM) as tcp:
            tcp.settimeout(remaining)
            tcp.connect((server, 53))
            tcp.sendall(struct.pack("!H", len(query)) + query)
            size_data = _recv_exact(tcp, 2)
            response = _recv_exact(tcp, struct.unpack("!H", size_data)[0])
    return response


def _recv_exact(connection: socket.socket, count: int) -> bytes:
    result = bytearray()
    while len(result) < count:
        chunk = connection.recv(count - len(result))
        if not chunk:
            raise ProxyError(502, "unexpected end of stream")
        result.extend(chunk)
    return bytes(result)


def _dns_lookup(name: str, record_type: int, deadline: float) -> list[str]:
    query_id = random.SystemRandom().randrange(65536)
    query = struct.pack("!HHHHHH", query_id, 0x0100, 1, 0, 0, 0) + _dns_name(name) + struct.pack("!HH", record_type, 1)
    last_error: Exception | None = None
    for server in _read_nameservers():
        try:
            response = _dns_exchange(server, query, deadline)
            if len(response) < 12:
                raise ProxyError(502, "malformed DNS response")
            response_id, flags, questions, answers, _, _ = struct.unpack("!HHHHHH", response[:12])
            if response_id != query_id or not flags & 0x8000 or flags & 0x000F:
                raise ProxyError(502, "DNS resolution failed")
            offset = 12
            for _ in range(questions):
                offset = _skip_dns_name(response, offset) + 4
            addresses: list[str] = []
            for _ in range(answers):
                offset = _skip_dns_name(response, offset)
                if offset + 10 > len(response):
                    raise ProxyError(502, "malformed DNS response")
                kind, dns_class, _, length = struct.unpack("!HHIH", response[offset : offset + 10])
                offset += 10
                data = response[offset : offset + length]
                if len(data) != length:
                    raise ProxyError(502, "malformed DNS response")
                if dns_class == 1 and kind == record_type and length in (4, 16):
                    addresses.append(socket.inet_ntop(socket.AF_INET if length == 4 else socket.AF_INET6, data))
                offset += length
            return addresses
        except (OSError, ProxyError) as error:
            last_error = error
    raise ProxyError(502, "DNS resolution failed") from last_error


def resolve_public(host: str) -> tuple[ipaddress.IPv4Address | ipaddress.IPv6Address, ...]:
    try:
        literal = ipaddress.ip_address(host)
        addresses = [literal]
    except ValueError:
        deadline = time.monotonic() + DNS_TIMEOUT
        addresses = [ipaddress.ip_address(value) for kind in (1, 28) for value in _dns_lookup(host, kind, deadline)]
    if not addresses or any(not is_public_destination(address) for address in addresses):
        raise ProxyError(403, "destination is not public")
    return tuple(dict.fromkeys(addresses))


def _connect(
    addresses: tuple[ipaddress.IPv4Address | ipaddress.IPv6Address, ...],
    port: int,
    deadline: float,
) -> socket.socket:
    last_error: OSError | None = None
    for address in addresses:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ProxyError(504, "connection timeout")
        family = socket.AF_INET6 if address.version == 6 else socket.AF_INET
        connection = socket.socket(family, socket.SOCK_STREAM)
        connection.settimeout(min(CONNECT_TIMEOUT, remaining))
        try:
            connection.connect((str(address), port))
            connection.settimeout(IDLE_TIMEOUT)
            return connection
        except OSError as error:
            last_error = error
            connection.close()
    raise ProxyError(502, "connection failed") from last_error


def _format_host(host: str, port: int) -> str:
    rendered = f"[{host}]" if ":" in host else host
    return rendered if port == 80 else f"{rendered}:{port}"


def _forward_headers(request: Request) -> bytes:
    connection_tokens: set[str] = set()
    for name, value in request.headers:
        if name == "connection":
            connection_tokens.update(token.strip().lower() for token in value.split(","))
    removed = {"connection", "proxy-connection", "proxy-authorization", "keep-alive", "te", "trailer", "upgrade", "host"}
    removed.update(connection_tokens)
    output = [f"{request.method} {request.path} {request.version}\r\n", f"Host: {_format_host(request.host, request.port)}\r\n"]
    output.extend(f"{name}: {value}\r\n" for name, value in request.headers if name not in removed)
    output.append("Connection: close\r\n\r\n")
    return "".join(output).encode("latin-1")


def _relay(left: socket.socket, right: socket.socket, deadline: float) -> None:
    selector = selectors.DefaultSelector()
    selector.register(left, selectors.EVENT_READ, right)
    selector.register(right, selectors.EVENT_READ, left)
    transferred = {left: 0, right: 0}
    try:
        while True:
            remaining = min(IDLE_TIMEOUT, deadline - time.monotonic())
            if remaining <= 0:
                return
            events = selector.select(remaining)
            if not events:
                return
            for key, _ in events:
                data = key.fileobj.recv(64 * 1024)
                if not data:
                    return
                transferred[key.fileobj] += len(data)
                if transferred[key.fileobj] > MAX_RELAY_BYTES:
                    return
                key.data.sendall(data)
    finally:
        selector.close()


class ProxyHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        client = self.request
        client.settimeout(IDLE_TIMEOUT)
        deadline = time.monotonic() + MAX_LIFETIME
        try:
            buffer = bytearray()
            while b"\r\n\r\n" not in buffer:
                lifetime_remaining = deadline - time.monotonic()
                if lifetime_remaining <= 0:
                    raise ProxyError(408, "request timeout")
                client.settimeout(min(IDLE_TIMEOUT, lifetime_remaining))
                chunk = client.recv(4096)
                if not chunk:
                    raise ProxyError(400, "incomplete request")
                buffer.extend(chunk)
                if len(buffer) > MAX_HEADER_BYTES:
                    raise ProxyError(431, "headers too large")
            boundary = buffer.index(b"\r\n\r\n") + 4
            request = parse_request(bytes(buffer[:boundary]))
            remainder = bytes(buffer[boundary:])
            addresses = resolve_public(request.host)
            upstream = _connect(addresses, request.port, deadline)
            try:
                if request.method == "CONNECT":
                    if request.content_length or remainder:
                        raise ProxyError(400, "CONNECT request body is not accepted")
                    client.sendall(b"HTTP/1.1 200 Connection Established\r\nConnection: close\r\n\r\n")
                    _relay(client, upstream, deadline)
                else:
                    if len(remainder) > request.content_length:
                        raise ProxyError(400, "unexpected bytes after request")
                    upstream.sendall(_forward_headers(request))
                    upstream.sendall(remainder)
                    remaining = request.content_length - len(remainder)
                    while remaining:
                        lifetime_remaining = deadline - time.monotonic()
                        if lifetime_remaining <= 0:
                            raise ProxyError(408, "request timeout")
                        client.settimeout(min(IDLE_TIMEOUT, lifetime_remaining))
                        chunk = client.recv(min(64 * 1024, remaining))
                        if not chunk:
                            raise ProxyError(400, "incomplete request body")
                        upstream.sendall(chunk)
                        remaining -= len(chunk)
                    _relay(client, upstream, deadline)
            finally:
                upstream.close()
        except ProxyError as error:
            response = f"HTTP/1.1 {error.status} {error.reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
            try:
                client.sendall(response.encode("ascii"))
            except OSError:
                pass
        except (OSError, TimeoutError):
            try:
                client.sendall(b"HTTP/1.1 502 upstream failure\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")
            except OSError:
                pass


class BoundedThreadingServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
    request_queue_size = MAX_CONCURRENCY

    def __init__(self, address: tuple[str, int]):
        super().__init__(address, ProxyHandler)
        self._slots = threading.BoundedSemaphore(MAX_CONCURRENCY)

    def process_request(self, request: socket.socket, client_address: tuple[str, int]) -> None:
        if not self._slots.acquire(blocking=False):
            request.close()
            return
        super().process_request(request, client_address)

    def process_request_thread(self, request: socket.socket, client_address: tuple[str, int]) -> None:
        try:
            super().process_request_thread(request, client_address)
        finally:
            self._slots.release()


def healthcheck() -> int:
    try:
        with socket.create_connection(("127.0.0.1", LISTEN_PORT), timeout=2) as connection:
            connection.sendall(b"BAD REQUEST\r\n\r\n")
            return 0 if connection.recv(64).startswith(b"HTTP/1.1 400") else 1
    except OSError:
        return 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--healthcheck", action="store_true")
    arguments = parser.parse_args()
    if arguments.healthcheck:
        return healthcheck()
    with BoundedThreadingServer((LISTEN_HOST, LISTEN_PORT)) as server:
        print("indexer egress guard listening", flush=True)
        server.serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
