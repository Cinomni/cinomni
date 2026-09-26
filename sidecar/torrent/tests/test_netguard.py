"""What the egress guard concludes, over inputs it never has to observe.

Every case here is a real failure mode of a tunnel, written as the three observations the sidecar
can make. None of them needs a tunnel, a network or a privilege, which is the point: the decision
that stops every download in the installation has to be testable without the thing it decides about.
"""

import os
import socket
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import netguard  # noqa: E402 - the path above is what makes this importable outside the image.

NOW = 1_800_000_000

# A routing table shaped exactly as the kernel writes it: header, then fixed columns. The default
# route is the row whose destination is all zeroes.
ROUTE_TABLE_VIA_TUNNEL = """Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT
tun0\t00000000\t0100000A\t0003\t0\t0\t0\t00000000\t0\t0\t0
eth0\t0000A8C0\t00000000\t0001\t0\t0\t0\t00FFFFFF\t0\t0\t0
"""

ROUTE_TABLE_VIA_ETHERNET = """Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT
eth0\t00000000\t0100A8C0\t0003\t0\t0\t0\t00000000\t0\t0\t0
"""

ROUTE_TABLE_NO_DEFAULT = """Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT
eth0\t0000A8C0\t00000000\t0001\t0\t0\t0\t00FFFFFF\t0\t0\t0
"""

ROUTE_TABLE_TWO_DEFAULTS = """Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT
eth0\t00000000\t0100A8C0\t0003\t0\t0\t100\t00000000\t0\t0\t0
tun0\t00000000\t0100000A\t0003\t0\t0\t50\t00000000\t0\t0\t0
"""

# /proc/net/if_inet6 as the kernel writes it: no header, the address as 32 hex characters, then the
# interface index, prefix length, scope, flags and the device name.
INET6_TABLE = """00000000000000000000000000000001 01 80 10 80       lo
20010db8000000000000000000000007 03 40 00 80     tun0
fe800000000000000000000000000001 03 40 20 80     tun0
20010db8beef000000000000deadbeef 02 40 00 80     eth0
"""


class DefaultRouteTests(unittest.TestCase):
    def test_reads_the_interface_of_the_default_route(self):
        self.assertEqual("tun0", netguard.parse_default_route(ROUTE_TABLE_VIA_TUNNEL))

    def test_reads_a_default_route_that_is_not_the_tunnel(self):
        self.assertEqual("eth0", netguard.parse_default_route(ROUTE_TABLE_VIA_ETHERNET))

    def test_returns_none_when_there_is_no_default_route(self):
        self.assertIsNone(netguard.parse_default_route(ROUTE_TABLE_NO_DEFAULT))

    def test_prefers_the_lowest_metric_when_two_default_routes_exist(self):
        # The failure this catches is a tunnel that installed its route without removing the old
        # one: both are default routes, and the kernel uses the cheaper. Reading the first row would
        # report the tunnel as in use while every packet left over the ethernet link.
        self.assertEqual("tun0", netguard.parse_default_route(ROUTE_TABLE_TWO_DEFAULTS))

    def test_tolerates_an_empty_or_truncated_table(self):
        self.assertIsNone(netguard.parse_default_route(""))
        self.assertIsNone(netguard.parse_default_route("Iface\tDestination\n"))
        self.assertIsNone(netguard.parse_default_route("tun0\t00000000\tbroken\n"))


class ClassifyTests(unittest.TestCase):
    def test_all_three_facts_holding_is_the_only_healthy_verdict(self):
        verdict = netguard.classify("tun0", True, "10.2.0.7", "tun0", "10.2.0.7", NOW)

        self.assertTrue(verdict.healthy)
        self.assertEqual(netguard.REASON_OK, verdict.reason)
        self.assertEqual("10.2.0.7", verdict.observed_source)
        self.assertEqual(NOW, verdict.checked_at)

    def test_a_tunnel_that_never_came_up_is_reported_as_a_missing_device(self):
        verdict = netguard.classify("tun0", False, None, None, None, NOW)

        self.assertFalse(verdict.healthy)
        self.assertFalse(verdict.tunnel_up)
        self.assertEqual(netguard.REASON_NO_DEVICE, verdict.reason)

    def test_an_absent_device_is_still_absent_when_another_route_exists(self):
        # The ordinary case, and the one an inference from "has no address" gets wrong: there is no
        # tunnel at all, and the container of course still has its own default route. Reporting this
        # as "the device has no address" sends an operator to look at a device that is not there.
        verdict = netguard.classify("tun0", False, None, "eth0", "192.168.1.20", NOW)

        self.assertEqual(netguard.REASON_NO_DEVICE, verdict.reason)

    def test_a_device_with_no_address_is_distinguished_from_an_absent_one(self):
        # The tunnel process is running and the interface exists, but no address has been assigned
        # yet. An operator chasing "device missing" would look in the wrong place.
        verdict = netguard.classify("tun0", True, None, "eth0", "192.168.1.20", NOW)

        self.assertFalse(verdict.healthy)
        self.assertEqual(netguard.REASON_NO_ADDRESS, verdict.reason)

    def test_a_tunnel_that_is_up_but_not_routed_is_not_healthy(self):
        verdict = netguard.classify("tun0", True, "10.2.0.7", "eth0", "192.168.1.20", NOW)

        self.assertFalse(verdict.healthy)
        self.assertTrue(verdict.tunnel_up)
        self.assertFalse(verdict.default_route_via_tunnel)
        self.assertEqual(netguard.REASON_ROUTE_ELSEWHERE, verdict.reason)

    def test_the_leak_case_the_first_two_facts_miss(self):
        # The device is up and holds the default route, and yet the kernel would put the household's
        # address on the next packet — a policy route or a second table. This is the whole reason
        # the source address is observed rather than inferred from the routing table.
        verdict = netguard.classify("tun0", True, "10.2.0.7", "tun0", "192.168.1.20", NOW)

        self.assertFalse(verdict.healthy)
        self.assertTrue(verdict.tunnel_up)
        self.assertTrue(verdict.default_route_via_tunnel)
        self.assertFalse(verdict.egress_identity_matches)
        self.assertEqual(netguard.REASON_IDENTITY_MISMATCH, verdict.reason)

    def test_no_route_at_all_is_reported_as_such_and_is_not_healthy(self):
        # What a sealed box looks like from inside: nothing can leave, including this probe.
        verdict = netguard.classify("tun0", True, "10.2.0.7", "tun0", None, NOW)

        self.assertFalse(verdict.healthy)
        self.assertEqual(netguard.REASON_NO_EGRESS, verdict.reason)

    def test_no_configured_device_means_the_guard_is_off_rather_than_failing(self):
        verdict = netguard.classify("", False, None, "eth0", "192.168.1.20", NOW)

        self.assertFalse(verdict.healthy)
        self.assertEqual(netguard.REASON_DISABLED, verdict.reason)
        self.assertEqual("", verdict.tunnel_device)


class Ipv6Tests(unittest.TestCase):
    """The leak class every IPv4-only probe above reports as verified."""

    def test_reads_the_addresses_of_one_interface_in_the_kernels_own_form(self):
        self.assertEqual(
            ["2001:db8::7", "fe80::1"], netguard.parse_inet6_addresses(INET6_TABLE, "tun0")
        )

    def test_an_interface_with_no_addresses_has_none(self):
        self.assertEqual([], netguard.parse_inet6_addresses(INET6_TABLE, "wg0"))
        self.assertEqual([], netguard.parse_inet6_addresses("", "tun0"))
        self.assertEqual([], netguard.parse_inet6_addresses(INET6_TABLE, ""))

    def test_a_truncated_or_corrupt_row_is_skipped_rather_than_fatal(self):
        self.assertEqual([], netguard.parse_inet6_addresses("nonsense\nshort tun0\n", "tun0"))

    def test_no_ipv6_egress_at_all_is_not_a_leak(self):
        # The ordinary container. A family that does not exist cannot carry traffic off the box, and
        # penalising it would hold every download on a correctly sealed installation.
        self.assertFalse(netguard.ipv6_egress_leaks(None, []))
        self.assertFalse(netguard.ipv6_egress_leaks("", ["2001:db8::7"]))

    def test_a_link_local_or_loopback_source_is_not_a_leak(self):
        self.assertFalse(netguard.ipv6_egress_leaks("fe80::1%eth0", []))
        self.assertFalse(netguard.ipv6_egress_leaks("::1", []))

    def test_a_source_the_tunnel_owns_is_not_a_leak(self):
        self.assertFalse(netguard.ipv6_egress_leaks("2001:db8::7", ["2001:db8::7", "fe80::1"]))

    def test_a_global_source_the_tunnel_does_not_own_is_a_leak(self):
        # The tunnel carries IPv4 only and native IPv6 egress survived it: libtorrent announces and
        # connects with the household's own address while every IPv4 fact still adds up.
        self.assertTrue(netguard.ipv6_egress_leaks("2001:db8:beef::dead:beef", ["2001:db8::7"]))

    def test_an_ipv6_leak_makes_an_otherwise_perfect_verdict_unverified(self):
        verdict = netguard.classify("tun0", True, "10.2.0.7", "tun0", "10.2.0.7", NOW, ipv6_leak=True)

        self.assertFalse(verdict.healthy)
        self.assertFalse(verdict.egress_identity_matches)
        self.assertEqual(netguard.REASON_IPV6_LEAK, verdict.reason)

    def test_the_ipv4_reason_wins_when_both_families_are_wrong(self):
        # An operator sent to the IPv6 story first would fix the smaller half of a broken tunnel.
        verdict = netguard.classify("tun0", True, "10.2.0.7", "eth0", "192.168.1.20", NOW, ipv6_leak=True)

        self.assertEqual(netguard.REASON_ROUTE_ELSEWHERE, verdict.reason)


class ObservationFailureTests(unittest.TestCase):
    """A probe that could not be taken. It is the answer that must never look like health."""

    def test_a_failed_observation_is_unhealthy_and_says_so(self):
        verdict = netguard.observation_failed("tun0", NOW)

        self.assertFalse(verdict.healthy)
        self.assertEqual(netguard.REASON_OBSERVATION_FAILED, verdict.reason)
        self.assertEqual("tun0", verdict.tunnel_device)

    def test_it_carries_a_fresh_timestamp_so_staleness_is_visible(self):
        # The trap this closes: leaving the previous verdict in place would let a guard that can no
        # longer observe anything keep publishing the last good answer for the life of the process.
        self.assertGreater(netguard.observation_failed("tun0").checked_at, 0)


class PolicyTests(unittest.TestCase):
    def test_the_three_modes_are_accepted_as_written(self):
        for policy in netguard.POLICIES:
            self.assertEqual(policy, netguard.normalize_policy(policy))

    def test_case_and_padding_do_not_change_the_mode(self):
        self.assertEqual(netguard.POLICY_IGNORE, netguard.normalize_policy("  Ignore \n"))

    def test_the_backend_spelling_of_the_same_word_is_the_same_setting(self):
        # One variable feeds this process and the backend, and they must not be able to end up
        # enforcing different rules because one of them spells the mode with capitals.
        for spelling in ("PauseAndAlert", "pause-and-alert", "pause_and_alert", "PAUSEANDALERT"):
            self.assertEqual(netguard.POLICY_PAUSE_AND_ALERT, netguard.normalize_policy(spelling))

    def test_an_unset_or_misspelt_policy_falls_back_to_the_strongest(self):
        # A typo must not be able to weaken an installation. This is the one default that is not
        # "leave it as it was": it is "assume the operator meant the safe thing".
        for value in (None, "", "   ", "blok", "off", "disabled", "true"):
            self.assertEqual(netguard.POLICY_BLOCK, netguard.normalize_policy(value))


class ProbeTests(unittest.TestCase):
    def test_the_source_probe_emits_nothing_and_still_answers(self):
        # A UDP connect performs the route lookup without a handshake. On any machine with a route
        # to the internet this returns an address; on one without, it returns None. Both are correct
        # answers and neither sends a packet — which is what makes it safe to run while leaking.
        source = netguard.observed_source_address()

        self.assertTrue(source is None or source.count(".") == 3)

    def test_an_absent_device_has_no_address(self):
        self.assertIsNone(netguard.device_address("cinomni-no-such-device"))

    def test_presence_is_observed_by_name_and_needs_no_privilege(self):
        self.assertFalse(netguard.device_present("cinomni-no-such-device"))
        self.assertFalse(netguard.device_present(""))

        # The positive case without hard-coding an interface name, which differs per platform: ask
        # the kernel what it has and confirm the probe agrees about the first one.
        existing = socket.if_nameindex()
        self.assertTrue(existing, "the machine running this has no network interfaces at all")
        self.assertTrue(netguard.device_present(existing[0][1]))

    def test_an_empty_device_name_is_not_an_error(self):
        self.assertIsNone(netguard.device_address(""))

    def test_an_unreadable_route_table_reads_as_empty_rather_than_raising(self):
        self.assertEqual("", netguard.read_route_table("/cinomni/no/such/route/table"))

    def test_observe_without_a_device_reports_the_guard_as_off(self):
        self.assertEqual(netguard.REASON_DISABLED, netguard.observe("").reason)

    def test_the_ipv6_source_probe_emits_nothing_and_still_answers(self):
        # Same silent lookup as the IPv4 one. On a machine without IPv6 it answers None, which is
        # "this namespace cannot leak over a family it does not have".
        source = netguard.observed_source_address_v6()

        self.assertTrue(source is None or ":" in source)

    def test_an_unreadable_inet6_table_reads_as_empty_rather_than_raising(self):
        self.assertEqual("", netguard.read_inet6_table("/cinomni/no/such/inet6/table"))

    def test_a_probe_under_descriptor_pressure_answers_rather_than_raising(self):
        # The failure that killed the guard thread: the socket was built outside the handler, so an
        # OSError from socket() escaped a probe documented as never failing.
        original = socket.socket

        def refuse(*args, **kwargs):
            raise OSError(24, "Too many open files")

        socket.socket = refuse
        try:
            self.assertIsNone(netguard.device_address("tun0"))
            self.assertIsNone(netguard.observed_source_address())
            self.assertIsNone(netguard.observed_source_address_v6())
        finally:
            socket.socket = original


if __name__ == "__main__":
    unittest.main()
