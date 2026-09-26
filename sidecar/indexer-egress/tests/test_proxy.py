import ipaddress
import socket
import threading
import time
import unittest

import proxy
from proxy import ProxyError, is_public_destination, parse_request


class DestinationTests(unittest.TestCase):
    def test_accepts_public_addresses(self):
        for address in ("1.1.1.1", "8.8.8.8", "2606:4700:4700::1111"):
            with self.subTest(address=address):
                self.assertTrue(is_public_destination(address))

    def test_rejects_non_public_and_metadata_addresses(self):
        addresses = (
            "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.0.1",
            "169.254.169.254", "100.64.0.1", "224.0.0.1", "192.0.2.1",
            "::1", "fc00::1", "fe80::1", "ff02::1", "2001:db8::1",
            "::ffff:127.0.0.1", "fd00:ec2::254",
        )
        for address in addresses:
            with self.subTest(address=address):
                self.assertFalse(is_public_destination(ipaddress.ip_address(address)))


class RequestParsingTests(unittest.TestCase):
    def test_parses_connect(self):
        request = parse_request(b"CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n")
        self.assertEqual(("CONNECT", "example.com", 443, None), (request.method, request.host, request.port, request.path))

    def test_parses_absolute_http_target(self):
        request = parse_request(b"GET http://example.com:8080/a?q=1 HTTP/1.1\r\nHost: ignored.example\r\n\r\n")
        self.assertEqual(("example.com", 8080, "/a?q=1"), (request.host, request.port, request.path))

    def test_rejects_ambiguous_or_unsafe_requests(self):
        requests = (
            b"GET /relative HTTP/1.1\r\nHost: example.com\r\n\r\n",
            b"GET https://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n",
            b"GET http://user@example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n",
            b"GET http://example.com/ HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n",
            b"POST http://example.com/ HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n",
            b"GET http://example.com/ HTTP/1.1\r\n folded: value\r\n\r\n",
            b"CONNECT example.com HTTP/1.1\r\nHost: example.com\r\n\r\n",
        )
        for request in requests:
            with self.subTest(request=request):
                with self.assertRaises(ProxyError):
                    parse_request(request)


class RelayTests(unittest.TestCase):
    def test_stops_before_relaying_a_chunk_that_exceeds_the_direction_limit(self):
        left_client, left_relay = socket.socketpair()
        right_relay, right_server = socket.socketpair()
        previous_limit = proxy.MAX_RELAY_BYTES
        proxy.MAX_RELAY_BYTES = 4
        try:
            worker = threading.Thread(
                target=proxy._relay,
                args=(left_relay, right_relay, time.monotonic() + 1),
            )
            worker.start()
            left_client.sendall(b"12345")
            worker.join(timeout=1)

            self.assertFalse(worker.is_alive())
            right_server.settimeout(0.05)
            with self.assertRaises(TimeoutError):
                right_server.recv(1)
        finally:
            proxy.MAX_RELAY_BYTES = previous_limit
            left_client.close()
            left_relay.close()
            right_relay.close()
            right_server.close()


if __name__ == "__main__":
    unittest.main()
