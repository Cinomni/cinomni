import unittest
from datetime import timedelta
from types import SimpleNamespace
from unittest import mock

import server


class DownloadSessionTests(unittest.TestCase):
    @mock.patch("server.lt.session")
    def test_direct_session_listens_on_the_container_network(self, session):
        server._download_session(None)

        settings = session.call_args.args[0]
        self.assertEqual("0.0.0.0:0", settings["listen_interfaces"])
        self.assertNotIn("outgoing_interfaces", settings)
        self.assertTrue(settings["announce_to_all_trackers"])
        self.assertTrue(settings["ssrf_mitigation"])
        self.assertFalse(settings["enable_dht"])

    @mock.patch("server.lt.session")
    def test_tunnel_session_remains_bound_to_the_device(self, session):
        server._download_session("tun0")

        settings = session.call_args.args[0]
        self.assertEqual("tun0:0", settings["listen_interfaces"])
        self.assertEqual("tun0", settings["outgoing_interfaces"])


class SeedingSecondsTests(unittest.TestCase):
    def test_libtorrent_2_duration_is_converted_to_seconds(self):
        status = SimpleNamespace(seeding_duration=timedelta(seconds=42))

        self.assertEqual(42, server._seeding_seconds(status))


class InfoHashTests(unittest.TestCase):
    def test_a_hybrid_or_v1_torrent_is_known_by_its_v1_hash(self):
        hashes = SimpleNamespace(v1="a" * 40, v2="b" * 64)

        self.assertEqual("a" * 40, server._hex_of(hashes))

    def test_a_v2_only_torrent_is_known_by_its_v2_hash_not_by_zeros(self):
        # Keyed by the all-zero v1, every v2-only torrent shared one identity.
        first = SimpleNamespace(v1="0" * 40, v2="c" * 64)
        second = SimpleNamespace(v1="0" * 40, v2="d" * 64)

        self.assertEqual("c" * 64, server._hex_of(first))
        self.assertNotEqual(server._hex_of(first), server._hex_of(second))


class MetadataNameTests(unittest.TestCase):
    def test_a_magnet_without_metadata_has_no_name_yet(self):
        # libtorrent reports the dn= here; it is the indexer's label, not the folder on disk.
        status = SimpleNamespace(has_metadata=False, name="Label.From.The.Indexer")

        self.assertEqual("", server._metadata_name(status))

    def test_the_name_travels_once_the_metadata_is_known(self):
        status = SimpleNamespace(has_metadata=True, name="Actual.Torrent.Folder")

        self.assertEqual("Actual.Torrent.Folder", server._metadata_name(status))


class _Aborted(Exception):
    """What `context.abort` raises, as grpc's own abort does, so a refused call stops where it is."""


def _context():
    context = mock.Mock()
    context.is_active.return_value = True
    context.abort.side_effect = lambda code, message: (_ for _ in ()).throw(_Aborted(code))
    return context


def _servicer(finished=False):
    engine = mock.Mock()
    engine.status.return_value = SimpleNamespace(is_finished=finished)
    return server.TorrentServicer(engine)


@mock.patch("server.time.sleep", lambda _: None)
class StatusStreamTests(unittest.TestCase):
    def test_a_stream_beyond_its_share_is_refused_rather_than_parked_on_a_worker(self):
        with mock.patch("server.STREAM_SLOTS", 2):
            servicer = _servicer()
        open_streams = [servicer.StreamStatus(SimpleNamespace(info_hash=f"h{i}"), _context()) for i in range(2)]
        for stream in open_streams:
            next(stream)

        with self.assertRaises(_Aborted) as refused:
            next(servicer.StreamStatus(SimpleNamespace(info_hash="h9"), _context()))

        self.assertEqual(server.grpc.StatusCode.RESOURCE_EXHAUSTED, refused.exception.args[0])

    def test_a_stream_ends_after_its_lifetime_and_gives_its_slot_back(self):
        with mock.patch("server.STREAM_SLOTS", 1):
            servicer = _servicer()

        with mock.patch("server.STREAM_LIFETIME_SECONDS", 0):
            first = list(servicer.StreamStatus(SimpleNamespace(info_hash="h1"), _context()))

        # A torrent that never finishes still lets go, so the one slot serves the next caller.
        self.assertEqual(1, len(first))
        self.assertIsNotNone(next(servicer.StreamStatus(SimpleNamespace(info_hash="h2"), _context())))

    def test_a_stream_the_caller_abandons_gives_its_slot_back(self):
        with mock.patch("server.STREAM_SLOTS", 1):
            servicer = _servicer()
        stream = servicer.StreamStatus(SimpleNamespace(info_hash="h1"), _context())
        next(stream)

        stream.close()  # what grpc does when the client cancels

        self.assertIsNotNone(next(servicer.StreamStatus(SimpleNamespace(info_hash="h2"), _context())))

    def test_an_unknown_torrent_gives_its_slot_back(self):
        with mock.patch("server.STREAM_SLOTS", 1):
            servicer = _servicer()
        servicer._engine.status.return_value = None

        with self.assertRaises(_Aborted):
            next(servicer.StreamStatus(SimpleNamespace(info_hash="gone"), _context()))

        servicer._engine.status.return_value = SimpleNamespace(is_finished=False)
        self.assertIsNotNone(next(servicer.StreamStatus(SimpleNamespace(info_hash="h2"), _context())))


class ResumeTrackerTests(unittest.TestCase):
    def test_announce_urls_a_resume_blob_carries_are_kept_with_their_tiers(self):
        params = SimpleNamespace(
            trackers=["udp://tracker.example:1337/announce", "https://t.example/announce"],
            tracker_tiers=[0, 1],
        )

        server._filter_resume_trackers(params)

        # Without them a re-added torrent finds no peers: DHT and local discovery are off in every mode.
        self.assertEqual(["udp://tracker.example:1337/announce", "https://t.example/announce"], params.trackers)
        self.assertEqual([0, 1], params.tracker_tiers)

    def test_announce_urls_that_are_not_trackers_are_dropped(self):
        params = SimpleNamespace(
            trackers=[
                "file:///etc/passwd",
                "javascript:alert(1)",
                "http://ok.example/announce",
                "http://split.example/a\nhttp://10.0.0.1/",
                "no-scheme.example/announce",
                "http://" + "x" * server.MAX_TRACKER_URL_LENGTH,
            ],
            tracker_tiers=[0, 1, 2, 3, 4, 5],
        )

        server._filter_resume_trackers(params)

        self.assertEqual(["http://ok.example/announce"], params.trackers)
        self.assertEqual([2], params.tracker_tiers)

    def test_announce_urls_aimed_inside_the_network_by_address_are_dropped(self):
        params = SimpleNamespace(
            trackers=[
                "http://127.0.0.1:8080/announce",
                "http://localhost/announce",
                "udp://192.168.1.1:6969/announce",
                "http://169.254.169.254/latest",
                "http://[::1]/announce",
                "http://user:pw@t.example/announce",
                "http://93.184.216.34/announce",
                "https://t.example:8443/announce",
            ],
            tracker_tiers=[0, 0, 0, 0, 0, 0, 1, 2],
        )

        server._filter_resume_trackers(params)

        # A name is left to libtorrent's ssrf_mitigation at connection time; an address literal the
        # blob states outright is refused here.
        self.assertEqual(["http://93.184.216.34/announce", "https://t.example:8443/announce"], params.trackers)
        self.assertEqual([1, 2], params.tracker_tiers)

    def test_a_resume_blob_cannot_bring_back_web_seeds(self):
        params = SimpleNamespace(url_seeds=["http://10.0.0.1/file"], http_seeds=["http://10.0.0.2/"])

        server._clear_resume_web_seeds(params)

        self.assertEqual([], params.url_seeds)
        self.assertEqual([], params.http_seeds)

    def test_a_resume_blob_cannot_carry_an_unbounded_announce_list(self):
        params = SimpleNamespace(
            trackers=[f"http://t{i}.example/announce" for i in range(server.MAX_RESUME_TRACKERS + 50)],
            tracker_tiers=[],
        )

        server._filter_resume_trackers(params)

        self.assertEqual(server.MAX_RESUME_TRACKERS, len(params.trackers))
        self.assertEqual([0] * server.MAX_RESUME_TRACKERS, params.tracker_tiers)


class ResumeTests(unittest.TestCase):
    def test_resuming_puts_the_torrent_back_under_the_sessions_queue(self):
        handle = mock.Mock()
        engine = SimpleNamespace(_lock=server.threading.Lock(), _handles={"h": handle})

        self.assertTrue(server.TorrentEngine.resume(engine, "h"))

        # Pausing clears auto_managed; a resume that left it cleared would carry the manual flag into
        # every later checkpoint, and so into every re-add.
        handle.set_flags.assert_called_once_with(server.lt.torrent_flags.auto_managed)
        handle.resume.assert_called_once_with()


def _bare_engine(policy=server.netguard.POLICY_BLOCK, device="tun0"):
    """An engine with its state but none of its threads or sessions, so one method can be driven."""
    engine = server.TorrentEngine.__new__(server.TorrentEngine)
    engine._tunnel_device = device
    engine._tunnel_policy = policy
    engine._download_session = mock.Mock()
    engine._seed_session = mock.Mock()
    engine._handles = {}
    engine._resume = {}
    engine._resume_ready = {}
    engine._lock = server.threading.Lock()
    engine._stop = server.threading.Event()
    engine._verdict = server.netguard.classify(device, False, None, None, None, 0)
    engine._session_held = False
    engine._hold_overridden = False
    return engine


def _unhealthy(reason=server.netguard.REASON_ROUTE_ELSEWHERE):
    return SimpleNamespace(healthy=False, reason=reason)


class SessionHoldTests(unittest.TestCase):
    def test_block_holds_the_whole_session_when_egress_is_not_verified(self):
        engine = _bare_engine(server.netguard.POLICY_BLOCK)

        engine._publish(_unhealthy())

        engine._download_session.pause.assert_called_once_with()

    def test_pause_and_alert_also_stops_the_traffic_at_once(self):
        # The kill-switch stays immediate: only the durable decision waits for a sustained failure.
        engine = _bare_engine(server.netguard.POLICY_PAUSE_AND_ALERT)

        engine._publish(_unhealthy())

        engine._download_session.pause.assert_called_once_with()

    def test_an_override_under_pause_and_alert_lifts_the_hold_for_the_rest_of_the_outage(self):
        # Without it the operator's documented override resumed a torrent that moved nothing inside a
        # held session.
        engine = _bare_engine(server.netguard.POLICY_PAUSE_AND_ALERT)
        engine._publish(_unhealthy())

        self.assertTrue(engine.override_hold())
        engine._download_session.resume.assert_called_once_with()

        engine._publish(_unhealthy())  # the same outage, observed again
        engine._download_session.pause.assert_called_once_with()

    def test_the_override_ends_when_egress_verifies_so_the_next_outage_is_stopped(self):
        engine = _bare_engine(server.netguard.POLICY_PAUSE_AND_ALERT)
        engine._publish(_unhealthy())
        engine.override_hold()

        engine._publish(SimpleNamespace(healthy=True, reason=server.netguard.REASON_OK))
        engine._publish(_unhealthy())

        self.assertEqual(2, engine._download_session.pause.call_count)

    def test_block_refuses_the_override(self):
        engine = _bare_engine(server.netguard.POLICY_BLOCK)
        engine._publish(_unhealthy())

        self.assertFalse(engine.override_hold())
        engine._download_session.resume.assert_not_called()

    def test_the_override_rpc_answers_failed_precondition_when_refused(self):
        engine = mock.Mock()
        engine.override_hold.return_value = False
        servicer = server.TorrentServicer(engine)

        with self.assertRaises(_Aborted) as refused:
            servicer.OverrideTunnelHold(SimpleNamespace(), _context())

        self.assertEqual(server.grpc.StatusCode.FAILED_PRECONDITION, refused.exception.args[0])

    def test_ignore_holds_nothing(self):
        engine = _bare_engine(server.netguard.POLICY_IGNORE)

        engine._publish(_unhealthy())

        engine._download_session.pause.assert_not_called()


@mock.patch("server.os._exit")
class OrphanedNamespaceTests(unittest.TestCase):
    def test_a_device_missing_past_the_limit_ends_the_process_after_holding_the_session(self, exit_):
        engine = _bare_engine()
        missing = _unhealthy(server.netguard.REASON_NO_DEVICE)

        since = engine._track_missing_device(missing, None, 1000.0)
        engine._track_missing_device(missing, since, 1000.0 + server.TUNNEL_ORPHAN_SECONDS - 1)
        exit_.assert_not_called()

        engine._track_missing_device(missing, since, 1000.0 + server.TUNNEL_ORPHAN_SECONDS)

        engine._download_session.pause.assert_called_once_with()
        exit_.assert_called_once()

    def test_a_device_that_comes_back_resets_the_count(self, exit_):
        engine = _bare_engine()
        missing = _unhealthy(server.netguard.REASON_NO_DEVICE)

        since = engine._track_missing_device(missing, None, 0.0)
        since = engine._track_missing_device(_unhealthy(server.netguard.REASON_OK), since, 10.0)
        self.assertIsNone(since)
        since = engine._track_missing_device(missing, since, 20.0)
        engine._track_missing_device(missing, since, 20.0 + server.TUNNEL_ORPHAN_SECONDS - 1)

        exit_.assert_not_called()

    def test_any_other_failure_never_ends_the_process(self, exit_):
        # A route through the household or a mismatched identity is the guard's to hold, not to exit on.
        engine = _bare_engine()
        leak = _unhealthy(server.netguard.REASON_ROUTE_ELSEWHERE)

        since = engine._track_missing_device(leak, None, 0.0)
        engine._track_missing_device(leak, since, server.TUNNEL_ORPHAN_SECONDS * 10)

        exit_.assert_not_called()


class AlertPumpTests(unittest.TestCase):
    def test_only_errors_storage_and_status_are_asked_for(self):
        self.assertEqual(0x49, server.ALERT_MASK)
        self.assertEqual(int(server.lt.alert.category_t.storage_notification), server.ALERT_STORAGE)
        self.assertEqual(int(server.lt.alert.category_t.status_notification), server.ALERT_STATUS)
        self.assertEqual(int(server.lt.alert.category_t.error_notification), server.ALERT_ERROR)

    def test_a_pass_that_fails_does_not_end_the_pump(self):
        engine = _bare_engine()
        passes = []

        def drain():
            passes.append(1)
            if len(passes) == 1:
                raise RuntimeError("a malformed alert")
            engine._stop.set()

        engine._drain_alerts = drain
        with mock.patch("server.time.sleep", lambda _: None):
            engine._pump_alerts()

        self.assertEqual(2, len(passes))


class RemoveTests(unittest.TestCase):
    def test_removing_a_torrent_drops_its_checkpoint_from_memory(self):
        engine = _bare_engine()
        engine._handles["h"] = mock.Mock()
        engine._resume["h"] = b"blob"
        engine._resume_ready["h"] = server.threading.Event()

        self.assertTrue(engine.remove("h", delete_files=False))

        self.assertNotIn("h", engine._resume)
        self.assertNotIn("h", engine._resume_ready)


class ErroredTorrentTests(unittest.TestCase):
    @staticmethod
    def _handle(error_value, save_path="/data/downloads/a"):
        handle = mock.Mock()
        handle.status.return_value = SimpleNamespace(
            errc=SimpleNamespace(value=lambda: error_value), save_path=save_path)
        return handle

    def test_adding_a_torrent_that_is_in_error_clears_it_and_starts_again(self):
        handle = self._handle(5)

        server._retry_if_errored(handle, "/data/downloads/a/")

        handle.move_storage.assert_not_called()
        handle.clear_error.assert_called_once_with()
        handle.resume.assert_called_once_with()

    def test_a_retry_for_a_new_task_moves_the_torrent_to_that_tasks_folder(self):
        # Resumed in place, it wrote into the failed task's folder and the new task imported nothing.
        handle = self._handle(5, save_path="/data/downloads/old-attempt")

        server._retry_if_errored(handle, "/data/downloads/new-attempt")

        handle.move_storage.assert_called_once_with("/data/downloads/new-attempt")
        handle.resume.assert_called_once_with()

    def test_a_healthy_torrent_is_left_as_it_is(self):
        # A paused one especially: resuming it here would undo an operator's pause.
        handle = self._handle(0)

        server._retry_if_errored(handle, "/data/downloads/other")

        handle.move_storage.assert_not_called()
        handle.clear_error.assert_not_called()
        handle.resume.assert_not_called()


class ResumeBlobFilterTests(unittest.TestCase):
    def test_a_resume_blob_cannot_switch_the_ip_filter_off(self):
        params = server.lt.add_torrent_params()
        params.flags &= ~server.lt.torrent_flags.apply_ip_filter

        server._enforce_ip_filter(params)

        self.assertTrue(params.flags & server.lt.torrent_flags.apply_ip_filter)

    def test_a_resume_blob_cannot_bring_peers_along(self):
        params = SimpleNamespace(peers=[("192.168.1.2", 6881)], banned_peers=[("10.0.0.1", 1)])

        server._clear_resume_peers(params)

        self.assertEqual([], params.peers)
        self.assertEqual([], params.banned_peers)


class QueuedTests(unittest.TestCase):
    def test_a_torrent_the_session_queued_is_waiting(self):
        flags = server.lt.torrent_flags.paused | server.lt.torrent_flags.auto_managed

        self.assertTrue(server._is_queued(SimpleNamespace(flags=flags)))

    def test_a_manual_pause_is_not_a_queue(self):
        self.assertFalse(server._is_queued(SimpleNamespace(flags=server.lt.torrent_flags.paused)))
        self.assertFalse(server._is_queued(SimpleNamespace(flags=server.lt.torrent_flags.auto_managed)))


class OrphanSecondsTests(unittest.TestCase):
    def test_an_unusable_limit_falls_back_to_the_default(self):
        for raw in (None, "", "nan", "inf", "-5", "0", "abc"):
            self.assertEqual(server.DEFAULT_TUNNEL_ORPHAN_SECONDS, server._orphan_seconds(raw), raw)

    def test_a_usable_limit_is_kept(self):
        self.assertEqual(30.0, server._orphan_seconds("30"))


class TestTorrentTests(unittest.TestCase):
    def test_the_test_torrent_surface_is_off_unless_the_development_topology_asks(self):
        servicer = server.TorrentServicer(mock.Mock())

        with mock.patch("server.TEST_TORRENTS_ENABLED", False):
            with self.assertRaises(_Aborted) as refused:
                servicer.CreateTestTorrent(SimpleNamespace(size_bytes=1, name="t"), _context())

        self.assertEqual(server.grpc.StatusCode.UNIMPLEMENTED, refused.exception.args[0])
        servicer._engine.create_test_torrent.assert_not_called()


class PrivateAddressFilterTests(unittest.TestCase):
    def test_the_household_network_and_this_machine_are_refused(self):
        ip_filter = server._private_address_filter(allow_loopback=False)

        for address in ("10.0.0.1", "192.168.1.1", "172.16.5.4", "169.254.169.254", "127.0.0.1",
                        "100.64.0.1", "::1", "fd00::1", "fe80::1", "224.0.0.1",
                        "::ffff:192.168.1.1", "::ffff:10.0.0.1", "64:ff9b::a00:1", "::a00:1"):
            self.assertEqual(server.IP_FILTER_BLOCKED, ip_filter.access(address), address)

    def test_public_peers_are_allowed(self):
        ip_filter = server._private_address_filter(allow_loopback=False)

        for address in ("93.184.216.34", "8.8.8.8", "2606:4700::1111"):
            self.assertEqual(0, ip_filter.access(address), address)

    def test_loopback_is_reachable_only_for_the_test_seeder(self):
        ip_filter = server._private_address_filter(allow_loopback=True)

        self.assertEqual(0, ip_filter.access("127.0.0.1"))
        self.assertEqual(server.IP_FILTER_BLOCKED, ip_filter.access("192.168.1.1"))

    @mock.patch("server.lt.session")
    def test_the_download_session_is_given_the_filter(self, session):
        server._download_session(None)

        session.return_value.set_ip_filter.assert_called_once()


if __name__ == "__main__":
    unittest.main()
