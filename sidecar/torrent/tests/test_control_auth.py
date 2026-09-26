"""The credential on the control port, as rules rather than as a running server."""

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import control_auth  # noqa: E402 - the path above is what makes this importable outside the image.

TOKEN = "b" * control_auth.MIN_TOKEN_LENGTH
OTHER = "c" * control_auth.MIN_TOKEN_LENGTH


class AuthorizationTests(unittest.TestCase):
    def test_the_right_token_is_accepted(self):
        metadata = ((control_auth.TOKEN_METADATA_KEY, TOKEN),)

        self.assertTrue(control_auth.is_authorized(metadata, TOKEN))

    def test_a_wrong_token_is_refused(self):
        metadata = ((control_auth.TOKEN_METADATA_KEY, OTHER),)

        self.assertFalse(control_auth.is_authorized(metadata, TOKEN))

    def test_no_metadata_at_all_is_refused(self):
        self.assertFalse(control_auth.is_authorized((), TOKEN))
        self.assertFalse(control_auth.is_authorized(None, TOKEN))

    def test_a_prefix_of_the_token_is_refused(self):
        metadata = ((control_auth.TOKEN_METADATA_KEY, TOKEN[:-1]),)

        self.assertFalse(control_auth.is_authorized(metadata, TOKEN))

    def test_another_header_carrying_the_token_does_not_authorize(self):
        metadata = (("authorization", TOKEN), ("x-token", TOKEN))

        self.assertFalse(control_auth.is_authorized(metadata, TOKEN))

    def test_the_key_is_matched_exactly_as_grpc_normalizes_it(self):
        # gRPC lower-cases metadata keys. A capitalised key never arrives, so it must not be treated
        # as if it had — otherwise the check would depend on a client's spelling.
        metadata = (("X-Cinomni-Control-Token", TOKEN),)

        self.assertFalse(control_auth.is_authorized(metadata, TOKEN))

    def test_a_non_ascii_value_is_refused_rather_than_raising(self):
        # A caller that speaks HTTP/2 directly can put anything in the header. Comparing str values
        # would raise TypeError here, which grpc turns into UNKNOWN — an unhandled path in the one
        # function whose whole job is to answer yes or no.
        for value in ("tökén" * 8, "\udcff" * 32, b"\xff" * 32):
            self.assertFalse(control_auth.is_authorized(((control_auth.TOKEN_METADATA_KEY, value),), TOKEN))

    def test_a_non_ascii_token_still_matches_itself(self):
        exotic = "tökén" * 8

        self.assertTrue(control_auth.is_authorized(((control_auth.TOKEN_METADATA_KEY, exotic),), exotic))

    def test_an_unconfigured_port_accepts_everything_by_design(self):
        # An empty expected token is a deployment choice, reported at startup, not a check that
        # failed open. The base topology keeps the port on a private network with no published port.
        self.assertTrue(control_auth.is_authorized((), ""))

    def test_the_first_matching_pair_is_the_one_read(self):
        metadata = (
            (control_auth.TOKEN_METADATA_KEY, TOKEN),
            (control_auth.TOKEN_METADATA_KEY, OTHER),
        )

        self.assertTrue(control_auth.is_authorized(metadata, TOKEN))

    def test_a_missing_value_is_not_a_match(self):
        metadata = ((control_auth.TOKEN_METADATA_KEY, None),)

        self.assertFalse(control_auth.is_authorized(metadata, TOKEN))


class ConfigurationTests(unittest.TestCase):
    def test_a_usable_token_passes(self):
        self.assertIsNone(control_auth.validate_configuration(TOKEN, required=True))

    def test_a_short_token_is_refused_before_it_can_be_guessed(self):
        message = control_auth.validate_configuration("short", required=False)

        self.assertIsNotNone(message)
        self.assertNotIn("short", message.replace("shorter", ""))

    def test_a_deployment_that_demands_a_token_will_not_start_without_one(self):
        # This is the rule that makes the VPN overlay safe: in a shared network namespace the port is
        # reachable from a network the operator does not own, so an empty token has to stop the start.
        self.assertIsNotNone(control_auth.validate_configuration("", required=True))

    def test_an_open_port_is_allowed_when_it_is_not_demanded(self):
        self.assertIsNone(control_auth.validate_configuration("", required=False))

    def test_no_message_ever_contains_the_token(self):
        for token, required in ((TOKEN, True), ("", True), ("short", False)):
            message = control_auth.validate_configuration(token, required) or ""
            self.assertNotIn(TOKEN, message)


class EnvironmentTests(unittest.TestCase):
    def test_the_token_is_read_and_trimmed(self):
        self.assertEqual(TOKEN, control_auth.read_token({"SIDECAR_CONTROL_TOKEN": f"  {TOKEN}\n"}))

    def test_an_absent_token_reads_as_empty(self):
        self.assertEqual("", control_auth.read_token({}))

    def test_only_an_explicit_one_demands_a_token(self):
        self.assertTrue(control_auth.token_required({"SIDECAR_REQUIRE_CONTROL_TOKEN": "1"}))
        for value in ("0", "", "true", "yes", "no"):
            self.assertFalse(control_auth.token_required({"SIDECAR_REQUIRE_CONTROL_TOKEN": value}))

    def test_no_interceptor_is_built_for_an_open_port(self):
        self.assertIsNone(control_auth.build_interceptor(""))


if __name__ == "__main__":
    unittest.main()
