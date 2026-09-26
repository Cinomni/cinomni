"""libtorrent sidecar (PoC).

Exposes a minimal libtorrent control surface over gRPC. The engine is BSD-3 licensed and
linked as a dependency (the only studied source we consume as code). A crash here never
takes down the .NET backend — that isolation is the whole point of the sidecar.

This PoC also runs an internal "seeder" session so the add -> progress -> complete flow can
be validated locally, with no internet and no third-party content.
"""

import ipaddress
import math
import os
import queue
import secrets
import tempfile
import threading
import time
from concurrent import futures

import grpc
import libtorrent as lt

import control_auth
import netguard
import paths
import torrent_pb2 as pb
import torrent_pb2_grpc as pb_grpc

LISTEN_ADDRESS = os.environ.get("SIDECAR_LISTEN", "[::]:50051")
DATA_ROOT = os.environ.get("SIDECAR_DATA", "/data")

# How often the egress guard re-observes. Five seconds is short enough that a dropped tunnel is
# caught before a meaningful amount of data leaves, and the observation costs three syscalls.
TUNNEL_POLL_SECONDS = float(os.environ.get("SIDECAR_TUNNEL_POLL_SECONDS", "5"))

# The largest test torrent the proof-of-concept surface will generate. It exists to bound one
# request's allocation, not to be a useful size: this method is a PoC affordance on a control port
# that also drives real downloads, and an unbounded length is a one-call denial of service.
MAX_TEST_TORRENT_BYTES = 64 * 1024 * 1024
# Largest gRPC message either way, matching the backend channel. The 4 MiB default is smaller than the
# resume data of a large torrent (its info dictionary rides in the blob): its checkpoint could never be
# saved, and a torrent could not be recovered from one.
MAX_MESSAGE_BYTES = 32 * 1024 * 1024
# A .torrent file is metadata; the backend never fetches one larger than this. The message limit above is
# sized for resume data, not for this.
MAX_TORRENT_FILE_BYTES = 8 * 1024 * 1024

# The control port's thread budget. The sync gRPC server runs every call on one worker thread, and a
# status stream holds its thread for as long as it stays open. Unbounded, one stream per in-flight
# torrent took every worker once there were as many torrents as threads, and from then on the calls
# that matter — add, pause, checkpoint and the egress question the kill-switch depends on — queued
# behind streams that never end. Streams therefore get a fixed share, the rest is reserved for
# ordinary calls, and a stream beyond its share is refused at once rather than parked on a thread.
STREAM_SLOTS = 12
UNARY_WORKERS = 8

# How long one status stream stays open. Bounded so the slots rotate: the backend re-subscribes
# whatever it still wants to watch, and a torrent that never finishes (paused, stalled, held) cannot
# keep a slot for the life of the process.
STREAM_LIFETIME_SECONDS = 30.0

# What a resume blob's announce list may carry back. The list reaches this process from storage, so it
# is filtered rather than trusted, but it is not dropped: DHT and local discovery are off in every mode,
# so a torrent re-added without trackers can only reach peers the blob happened to remember, and a
# magnet checkpointed before its metadata arrived could never resolve at all.
TRACKER_SCHEMES = ("http", "https", "udp")
MAX_RESUME_TRACKERS = 100
MAX_TRACKER_URL_LENGTH = 2048

# Alert categories worth receiving: errors, storage (where the resume-data alerts live) and status.
# Plain bit values rather than the enum, whose Python names differ between libtorrent 1.2 and 2.0; the
# bits themselves are the same in both. Every category used to be on, so the session built — and the
# pump then discarded — an alert per peer, per tracker reply and per piece, all of them swarm-driven.
ALERT_ERROR = 0x1
ALERT_STORAGE = 0x8
ALERT_STATUS = 0x40
ALERT_MASK = ALERT_ERROR | ALERT_STORAGE | ALERT_STATUS

# The test-torrent surface is a proof-of-concept affordance: it writes a file of random bytes and seeds
# it from a second session. It answers only where the development topology asks for it.
TEST_TORRENTS_ENABLED = os.environ.get("SIDECAR_ENABLE_TEST_TORRENTS", "").strip() == "1"

# How long the tunnel device may be missing before this process exits so its restart policy can start
# it again. Sharing a tunnel container's network namespace ties this process to that container's
# namespace as it was at start: when the tunnel container restarts it gets a new one, and this process
# is left in the old one — no device, no route, nothing to observe and nothing that would ever come
# back. Failing closed there is correct but permanent; exiting is what lets the restart rejoin.
DEFAULT_TUNNEL_ORPHAN_SECONDS = 120.0


def _orphan_seconds(raw):
    """The configured limit when it is a finite positive number of seconds, otherwise the default:
    `nan` would never exit and a negative value would exit on the first missing poll."""
    try:
        value = float(raw)
    except (TypeError, ValueError):
        return DEFAULT_TUNNEL_ORPHAN_SECONDS
    return value if math.isfinite(value) and value > 0 else DEFAULT_TUNNEL_ORPHAN_SECONDS


TUNNEL_ORPHAN_SECONDS = _orphan_seconds(os.environ.get("SIDECAR_TUNNEL_ORPHAN_SECONDS"))

# Peer and tracker addresses libtorrent must never connect to: this machine, its networks and the
# ranges that are not addresses at all. A swarm names peers, and a peer list that points at the router,
# the host or the backend would otherwise turn this process into a scanner of the household's network.
# `ssrf_mitigation` covers trackers and web seeds by name; this covers every connection by address.
BLOCKED_RANGES = (
    ("0.0.0.0", "0.255.255.255"),
    ("10.0.0.0", "10.255.255.255"),
    ("100.64.0.0", "100.127.255.255"),
    ("127.0.0.0", "127.255.255.255"),
    ("169.254.0.0", "169.254.255.255"),
    ("172.16.0.0", "172.31.255.255"),
    ("192.0.0.0", "192.0.0.255"),
    ("192.168.0.0", "192.168.255.255"),
    ("198.18.0.0", "198.19.255.255"),
    ("224.0.0.0", "255.255.255.255"),
    ("::", "::ffff:ffff"),  # unspecified, loopback and the deprecated IPv4-compatible block
    ("::ffff:0.0.0.0", "::ffff:255.255.255.255"),  # IPv4-mapped: the ranges above, spelt as IPv6
    ("64:ff9b::", "64:ff9b::ffff:ffff"),  # NAT64: the same, through a translator
    ("fc00::", "fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
    ("fe80::", "febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
    ("ff00::", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"),
)
LOOPBACK_RANGE = ("127.0.0.0", "127.255.255.255")
IP_FILTER_BLOCKED = 1


def _local_session():
    """A session bound to localhost with DHT/LSD/UPnP off — deterministic and leak-free.

    This is the proof-of-concept's internal seeder, which exists so the add → complete flow can be
    exercised with no internet and no third-party content. It is never the download session when a
    tunnel device is configured.
    """
    return lt.session(
        {
            "listen_interfaces": "127.0.0.1:0",
            "enable_dht": False,
            "enable_lsd": False,
            "enable_upnp": False,
            "enable_natpmp": False,
            "alert_mask": ALERT_MASK,
        }
    )


def _download_session(tunnel_device):
    """The session that talks to the swarm, bound to the tunnel device when there is one.

    Binding by *device* rather than by address is the point. An address binding breaks the moment
    the provider hands out a new one, and worse, it silently stops binding at all — libtorrent falls
    back to whatever the routing table offers. Naming the interface keeps the binding attached to
    the tunnel itself, so a tunnel that goes away takes the sockets with it instead of letting them
    re-route over the household's connection.

    `outgoing_interfaces` is the half that matters for peer connections we initiate, which is most
    of them; `listen_interfaces` is the half that matters for incoming ones and for the announce.
    Both are set, because either one alone leaves a direction unbound.

    Peer discovery stays off in every mode. DHT, local discovery and port mapping each talk to
    something the tunnel was chosen to hide from, and none of them are needed to fetch a release
    from a tracker.
    """
    settings = {
        # Magnet links commonly put every tracker in one tier. Trying only the first makes one dead
        # tracker block metadata discovery even when later trackers have peers.
        "announce_to_all_trackers": True,
        # libtorrent's own guard against trackers and web seeds that point into the local network. It
        # is the default today; it is named so a future default can never quietly turn it off.
        "ssrf_mitigation": True,
        "enable_dht": False,
        "enable_lsd": False,
        "enable_upnp": False,
        "enable_natpmp": False,
        "alert_mask": ALERT_MASK,
    }

    if tunnel_device:
        settings["listen_interfaces"] = f"{tunnel_device}:0"
        settings["outgoing_interfaces"] = tunnel_device
    else:
        # Tracker announces inherit the address of a listen socket. Loopback here makes otherwise
        # reachable trackers skip the announce, leaving magnets unable to discover metadata peers.
        settings["listen_interfaces"] = "0.0.0.0:0"

    session = lt.session(settings)
    session.set_ip_filter(_private_address_filter(allow_loopback=TEST_TORRENTS_ENABLED))
    return session


def _private_address_filter(allow_loopback):
    """An ip_filter refusing every range in BLOCKED_RANGES, trackers included (libtorrent's default).

    Loopback stays reachable only where the test-torrent surface is on: its seeder listens on
    127.0.0.1, and the development flow connects the download session to it by address.
    """
    ip_filter = lt.ip_filter()
    for first, last in BLOCKED_RANGES:
        if allow_loopback and (first, last) == LOOPBACK_RANGE:
            continue
        ip_filter.add_rule(first, last, IP_FILTER_BLOCKED)
    return ip_filter


def _observe_safely(device):
    """One egress observation that never raises.

    Every caller here is a place where an exception would be read as silence: the guard thread would
    die, the startup check would crash with a traceback instead of a decision. A probe that could not
    be taken is an unverified verdict, which is the answer that fails closed.
    """
    try:
        return netguard.observe(device)
    except Exception as error:  # noqa: BLE001 - the guard must survive every failure, named or not
        print(
            f"tunnel egress could not be observed ({type(error).__name__}: {error}); "
            "treating it as unverified",
            flush=True,
        )
        return netguard.observation_failed(device)


def _info_hash(handle):
    """Hex info-hash, tolerant of the 1.2 (info_hash) vs 2.0 (info_hashes) APIs."""
    try:
        return _hex_of(handle.info_hashes())
    except AttributeError:
        return str(handle.info_hash())


def _hex_of(info_hashes):
    """The v1 hash when the torrent has one, the v2 hash when it is v2-only.

    A v2-only torrent has an all-zero v1: keyed by it, every such torrent shared the identity
    0000…0000, so a second one attached to the first's task and was never downloaded.
    """
    v1 = str(info_hashes.v1)
    if v1.strip("0"):
        return v1
    return str(info_hashes.v2)


def _state_name(status):
    state = status.state
    return getattr(state, "name", str(state))


def _metadata_name(status):
    """The torrent's name once its metadata is known, and "" before it.

    Before the metadata arrives libtorrent reports the magnet's dn= as the name. That is a label the
    indexer chose, not the folder the torrent writes, and the backend builds the path Import scans
    from whatever name it is given first.
    """
    if not getattr(status, "has_metadata", False):
        return ""
    return status.name or ""


def _seeding_seconds(status):
    """Seconds spent seeding, tolerant of the 2.0 (seeding_duration) vs 1.2 (seeding_time) APIs."""
    seeding = getattr(status, "seeding_duration", None)
    if seeding is None:
        seeding = getattr(status, "seeding_time", 0)
    if hasattr(seeding, "total_seconds"):
        seeding = seeding.total_seconds()
    return int(seeding or 0)


def _clear_resume_trackers(params):
    """Drops the announce list a resume blob carries, tolerating the 1.2/2.0 attribute differences."""
    for field in ("trackers", "tracker_tiers"):
        try:
            setattr(params, field, [])
        except (AttributeError, TypeError):
            continue


def _is_acceptable_tracker(url):
    """An announce URL of a scheme libtorrent announces over, bounded, with nothing smuggled in it, and
    not naming a host inside this machine or its network by address.
    """
    if not isinstance(url, str) or not url or len(url) > MAX_TRACKER_URL_LENGTH:
        return False
    if any(ch.isspace() or ord(ch) < 0x20 or ord(ch) == 0x7F for ch in url):
        return False
    scheme, separator, rest = url.partition("://")
    if not separator or not rest or scheme.lower() not in TRACKER_SCHEMES:
        return False
    return _is_public_host(rest.split("/", 1)[0])


def _is_public_host(authority):
    """False for userinfo, `localhost`, and any address literal that is not globally routable.

    A name is let through: resolving it here would answer for this moment and not for the announce,
    and libtorrent's `ssrf_mitigation` is what guards names at connection time. What this closes is the
    case a resume blob can state outright — an announce aimed at the router, the host or the backend.
    """
    if "@" in authority:
        return False
    if authority.startswith("["):
        host = authority[1:].split("]", 1)[0]
    else:
        host = authority.rsplit(":", 1)[0] if authority.count(":") == 1 else authority
    if not host or host.lower().rstrip(".") in ("localhost", "localhost.localdomain"):
        return False
    try:
        return ipaddress.ip_address(host).is_global
    except ValueError:
        return True  # a name, not an address


def _filter_resume_trackers(params):
    """Keeps the announce URLs a resume blob carries that pass `_is_acceptable_tracker`, tiers intact.

    Anything this cannot read or write back is cleared instead: a list that could not be filtered is
    not one to hand to the session unfiltered.
    """
    try:
        trackers = list(params.trackers)
        tiers = list(getattr(params, "tracker_tiers", []))
    except (AttributeError, TypeError):
        _clear_resume_trackers(params)
        return

    kept, kept_tiers = [], []
    for index, url in enumerate(trackers):
        if len(kept) >= MAX_RESUME_TRACKERS:
            break
        if _is_acceptable_tracker(url):
            kept.append(url)
            kept_tiers.append(tiers[index] if index < len(tiers) else 0)

    try:
        params.trackers = kept
        params.tracker_tiers = kept_tiers
    except (AttributeError, TypeError):
        _clear_resume_trackers(params)


def _clear_resume_web_seeds(params):
    """Drops the web seeds a resume blob carries. Unlike trackers they are not needed to find peers,
    so nothing is lost by refusing them, and each one is an arbitrary URL this process would fetch.
    """
    for field in ("url_seeds", "http_seeds"):
        try:
            setattr(params, field, [])
        except (AttributeError, TypeError):
            continue


def _retry_if_errored(handle, save_path):
    """Gives a torrent in error a fresh start when it is added again.

    Adding a torrent the session already holds returns the handle it has, error and all. A failed
    download stayed in that state until the process restarted, so retrying the same release — which is
    what the backend does once its goal searches again — failed at once with the old error. The retry
    belongs to a new task with a folder of its own, so the torrent is moved there first: resumed where
    it was, it would write to the failed task's folder and the new one would import an empty one.
    """
    status = handle.status()
    if not (status.errc and status.errc.value() != 0):
        return
    current = getattr(status, "save_path", save_path) or save_path
    if os.path.normpath(current) != os.path.normpath(save_path):
        handle.move_storage(save_path)
    handle.clear_error()
    handle.resume()


def _enforce_ip_filter(params):
    """Sets the flag that makes the session's ip_filter apply to this torrent, whatever its source
    says. A resume blob carries its own flags, and one written with the flag cleared would otherwise
    connect to any address, the household's network included."""
    try:
        params.flags |= lt.torrent_flags.apply_ip_filter
    except AttributeError:
        pass  # libtorrent 1.2 has no per-torrent flag; the filter applies to every torrent there


def _clear_resume_peers(params):
    """Drops the peers a resume blob remembers. They reach this process from storage, like its
    trackers, and unlike them nothing is lost: the trackers the blob keeps name the swarm again."""
    for field in ("peers", "banned_peers"):
        try:
            setattr(params, field, [])
        except (AttributeError, TypeError):
            continue


def _is_queued(status):
    """True when the session's own queue paused the torrent (paused while auto-managed): it is waiting
    for a slot. A manual pause clears auto_managed first (see `pause`), so it never reads as queued."""
    try:
        flags = status.flags
        return bool(flags & lt.torrent_flags.paused) and bool(flags & lt.torrent_flags.auto_managed)
    except AttributeError:
        return bool(getattr(status, "paused", False)) and bool(getattr(status, "auto_managed", False))


def _is_paused(status):
    """True when the torrent is paused, tolerant of the flags-based (2.0) vs paused (1.2) APIs."""
    try:
        return bool(status.flags & lt.torrent_flags.paused)
    except AttributeError:
        return bool(getattr(status, "paused", False))


class TorrentEngine:
    """Owns the libtorrent sessions and pumps their alerts on a background thread."""

    def __init__(self, tunnel_device="", tunnel_policy=netguard.POLICY_BLOCK):
        self._tunnel_device = tunnel_device
        self._tunnel_policy = tunnel_policy
        self._download_session = _download_session(tunnel_device)
        # The PoC's internal seeder exists only where test torrents are on; elsewhere it would be a
        # second libtorrent session listening for nothing.
        self._seed_session = _local_session() if TEST_TORRENTS_ENABLED else None
        self._handles = {}  # info_hash -> torrent_handle (download session)
        self._resume = {}  # info_hash -> bytes (latest saved resume data)
        self._resume_ready = {}  # info_hash -> threading.Event
        self._lock = threading.Lock()
        self._stop = threading.Event()
        # The starting verdict is "nothing observed yet", which classifies as unhealthy. The guard
        # thread replaces it within one poll; until then, unhealthy is the honest answer.
        self._verdict = netguard.classify(tunnel_device, False, None, None, None, int(time.time()))
        self._session_held = False
        self._hold_overridden = False
        self._pump = threading.Thread(target=self._pump_alerts, daemon=True)
        self._pump.start()
        self._guard = threading.Thread(target=self._watch_tunnel, daemon=True)
        self._guard.start()

    # -- egress guard -------------------------------------------------------------------------

    def _watch_tunnel(self):
        """Re-observes the egress path and holds the whole session the moment it stops adding up.

        The hold is session-wide (`session.pause()`), not per torrent, and that is deliberate: it
        leaves every torrent's own paused flag untouched, so releasing the hold cannot resume a
        download an operator paused by hand. The backend owns the durable decision about each task;
        this is the immediate stop, applied in the process that owns the sockets and therefore
        applied before the backend has even noticed.

        Nothing in this loop is allowed to end it, save one exit on purpose (see `_exit_if_orphaned`).
        A guard thread that died would keep the last verdict frozen and healthy — `GetTunnelHealth`
        would answer from it, the backend would record a verified egress, and both halves of the
        kill-switch would be off with no signal at all. So every failure becomes an unverified verdict
        and the loop goes round again.
        """
        if not self._tunnel_device:
            return  # no tunnel configured: there is nothing to observe and nothing to hold

        missing_since = None
        while not self._stop.is_set():
            verdict = self._observe()
            self._publish(verdict)
            missing_since = self._track_missing_device(verdict, missing_since, time.monotonic())
            self._stop.wait(TUNNEL_POLL_SECONDS)

    def _track_missing_device(self, verdict, missing_since, now):
        """Returns when the device went missing (None while it is there), exiting once it has been
        missing for longer than TUNNEL_ORPHAN_SECONDS."""
        if verdict.reason != netguard.REASON_NO_DEVICE:
            return None
        if missing_since is None:
            return now
        if now - missing_since >= TUNNEL_ORPHAN_SECONDS:
            self._exit_if_orphaned()
        return missing_since

    def _exit_if_orphaned(self):
        """Ends the process so its restart policy starts it again inside the tunnel's current namespace.

        Only a device that has been missing this long gets here: a tunnel that reconnects recreates its
        device within seconds. The session is held first, so the exit adds nothing to what the missing
        device already stopped, and the backend re-establishes the transfers once the new process
        verifies its egress — the same path as any sidecar restart.
        """
        print(
            f"tunnel device {self._tunnel_device} has been missing for {TUNNEL_ORPHAN_SECONDS:.0f}s; "
            "exiting so the restart joins the tunnel's current network namespace",
            flush=True,
        )
        try:
            self._download_session.pause()
        finally:
            os._exit(3)

    def _observe(self):
        """One observation, and never an exception: failing to look is itself an unverified verdict."""
        return _observe_safely(self._tunnel_device)

    def _publish(self, verdict):
        """Records a verdict, logs the edge, and applies the hold it implies.

        A transition is logged once per edge. A tunnel that flaps would otherwise write a line every
        poll, which is how a real incident gets buried.
        """
        with self._lock:
            previous = self._verdict
            self._verdict = verdict

        if verdict.healthy != previous.healthy or verdict.reason != previous.reason:
            print(
                f"tunnel egress {'verified' if verdict.healthy else 'NOT verified'}: "
                f"{verdict.reason} (device {self._tunnel_device}, policy {self._tunnel_policy})",
                flush=True,
            )

        # Every policy but `ignore` stops the traffic on the first failed observation. Under
        # `pause-and-alert` an operator may lift that for the rest of the outage (`override_hold`); a
        # verified observation ends the override, so the next outage is stopped again.
        with self._lock:
            if verdict.healthy:
                self._hold_overridden = False
            overridden = self._hold_overridden
        try:
            self._apply_hold(
                not verdict.healthy and self._tunnel_policy != netguard.POLICY_IGNORE and not overridden)
        except Exception as error:  # noqa: BLE001 - a failed hold must be retried, not fatal
            print(f"could not apply the egress hold ({type(error).__name__}: {error})", flush=True)

    def override_hold(self):
        """Lifts the session hold for the rest of the current outage, under `pause-and-alert` only.

        This is the half of the operator's override that lives here: the backend resumes the one
        download it was asked for, and without this the torrent would move nothing inside a held
        session. Returns False under any other policy — under `block` no manual action puts traffic
        back on an unverified path, and under `ignore` there is nothing to lift.
        """
        if self._tunnel_policy != netguard.POLICY_PAUSE_AND_ALERT:
            return False
        with self._lock:
            self._hold_overridden = True
        print("egress hold overridden by an operator: torrent traffic resumes while unverified", flush=True)
        self._apply_hold(False)
        return True

    def _apply_hold(self, should_hold):
        """Pauses or releases the download session, only on an actual change of state.

        The flag moves after the session call returns, not before: recording a hold that never
        happened would stop this loop from ever trying again.
        """
        with self._lock:
            if should_hold == self._session_held:
                return

        if should_hold:
            self._download_session.pause()
        else:
            self._download_session.resume()

        with self._lock:
            self._session_held = should_hold

        print(
            "download session held: torrent traffic stopped"
            if should_hold
            else "download session released: torrent traffic resumed",
            flush=True,
        )

    def tunnel_observation(self):
        """The latest verdict and whether the session is held, as one consistent pair."""
        with self._lock:
            return self._verdict, self._session_held

    def _pump_alerts(self):
        """Drains both sessions' alerts until shutdown.

        One failure costs one pass, never the thread: a pump that died on a malformed alert would stop
        every later checkpoint from arriving, and each save would then wait out its timeout for good.
        """
        while not self._stop.is_set():
            try:
                self._drain_alerts()
            except Exception as error:  # noqa: BLE001 - the pump must outlive any single alert
                print(f"alert pump pass failed ({type(error).__name__}: {error})", flush=True)
            time.sleep(0.1)

    def _drain_alerts(self):
        for session in (self._download_session, self._seed_session):
            if session is None:
                continue
            for alert in session.pop_alerts():
                if isinstance(alert, lt.save_resume_data_alert):
                    self._on_resume_data(alert)

    def _on_resume_data(self, alert):
        buffer = lt.write_resume_data_buf(alert.params)
        try:
            info_hash = _hex_of(alert.params.info_hashes)
        except AttributeError:
            info_hash = str(alert.handle.info_hash())
        with self._lock:
            # Under the key the torrent was registered with. A hybrid torrent added from a v2-only magnet
            # is registered by its v2 hash and reports a v1 hash once its metadata arrives; keyed by that,
            # its checkpoint would never be found and every save would time out.
            info_hash = next(
                (key for key, handle in self._handles.items() if handle == alert.handle), info_hash)
            self._resume[info_hash] = bytes(buffer)
            event = self._resume_ready.setdefault(info_hash, threading.Event())
        event.set()

    # -- operations ---------------------------------------------------------------------

    def add(self, source_kind, source_value, save_path, peers):
        # Where this process writes is decided here and nowhere else. The caller's save path is
        # confined to the data root, and a resume blob's own embedded save path is overwritten with
        # the confined one rather than trusted: a `.fastresume` is a bencoded dictionary that names
        # a directory and a tracker list, and it reaches this process from storage the swarm's own
        # data influenced. Its announce list is filtered for the same reason, and kept for a better
        # one: with DHT and local discovery off, a re-added torrent without trackers finds no peers.
        # The announces still leave through the same session, bound to the tunnel when there is one.
        confined = paths.confine(DATA_ROOT, save_path)
        if confined is None:
            raise ValueError(paths.REJECTED)

        params = self._build_params(source_kind, source_value)
        params.save_path = confined
        _enforce_ip_filter(params)
        if source_kind == "resume_data":
            _filter_resume_trackers(params)
            _clear_resume_web_seeds(params)
            _clear_resume_peers(params)

        os.makedirs(confined, exist_ok=True)
        # The check and the use are separated in time: `confine` answered about a path, and what is
        # handed to libtorrent is a directory that exists now. Re-assert it on the directory as
        # opened, so a component swapped for a symlink between the two cannot redirect the write.
        if not paths.opened_directory_is_inside(DATA_ROOT, confined):
            raise ValueError(paths.REJECTED)

        handle = self._download_session.add_torrent(params)
        info_hash = _info_hash(handle)
        with self._lock:
            self._handles[info_hash] = handle
        _retry_if_errored(handle, confined)

        for peer in peers:
            handle.connect_peer((peer.host, peer.port))

        status = handle.status()
        resumed = source_kind == "resume_data"
        return info_hash, _metadata_name(status), resumed

    def _build_params(self, source_kind, source_value):
        if source_kind == "magnet_uri":
            return lt.parse_magnet_uri(source_value)
        if source_kind == "torrent_file":
            info = lt.torrent_info(lt.bdecode(source_value))
            params = lt.add_torrent_params()
            params.ti = info
            return params
        if source_kind == "resume_data":
            return lt.read_resume_data(source_value)
        raise ValueError(f"unknown source kind: {source_kind}")

    def status(self, info_hash):
        with self._lock:
            handle = self._handles.get(info_hash)
        if handle is None:
            return None
        return self._to_status(info_hash, handle.status())

    def _to_status(self, info_hash, status):
        error = ""
        if status.errc and status.errc.value() != 0:
            error = status.errc.message()
        return pb.TorrentStatus(
            info_hash=info_hash,
            name=_metadata_name(status),
            state=_state_name(status),
            progress=status.progress,
            total_done=status.total_done,
            total_wanted=status.total_wanted,
            download_rate=status.download_rate,
            upload_rate=status.upload_rate,
            num_peers=status.num_peers,
            num_seeds=status.num_seeds,
            is_finished=status.is_finished,
            error=error,
            all_time_upload=getattr(status, "all_time_upload", 0),
            all_time_download=getattr(status, "all_time_download", 0),
            seeding_seconds=_seeding_seconds(status),
            is_paused=_is_paused(status),
            is_queued=_is_queued(status),
        )

    def save_resume(self, info_hash, timeout=10.0):
        with self._lock:
            handle = self._handles.get(info_hash)
            event = self._resume_ready.setdefault(info_hash, threading.Event())
            event.clear()
        if handle is None:
            return None

        flags = lt.save_resume_flags_t.save_info_dict
        handle.save_resume_data(flags)
        if not event.wait(timeout):
            return None
        with self._lock:
            return self._resume.get(info_hash)

    def remove(self, info_hash, delete_files):
        with self._lock:
            handle = self._handles.pop(info_hash, None)
            # The last checkpoint went with the torrent: kept, it was a resume blob held in memory for
            # the life of the process, one per torrent ever removed.
            self._resume.pop(info_hash, None)
            self._resume_ready.pop(info_hash, None)
        if handle is None:
            return False
        flags = lt.session.delete_files if delete_files else 0
        self._download_session.remove_torrent(handle, flags)
        return True

    def pause(self, info_hash):
        with self._lock:
            handle = self._handles.get(info_hash)
        if handle is None:
            return False
        # Clear auto_managed first so libtorrent does not auto-resume a manual pause.
        try:
            handle.unset_flags(lt.torrent_flags.auto_managed)
        except AttributeError:
            pass
        handle.pause()
        return True

    def resume(self, info_hash):
        with self._lock:
            handle = self._handles.get(info_hash)
        if handle is None:
            return False
        # The counterpart of `pause`, which clears auto_managed: without restoring it the torrent runs
        # outside the session's queue, and a later checkpoint would carry the manual flag forward into
        # every re-add. The session-wide egress hold is separate and untouched by this.
        try:
            handle.set_flags(lt.torrent_flags.auto_managed)
        except AttributeError:
            pass
        handle.resume()
        return True

    def set_file_priorities(self, info_hash, priorities):
        with self._lock:
            handle = self._handles.get(info_hash)
        if handle is None:
            return False
        for entry in priorities:
            handle.file_priority(entry.file_index, entry.priority)
        return True

    def list_files(self, info_hash):
        with self._lock:
            handle = self._handles.get(info_hash)
        if handle is None:
            return None

        info = handle.torrent_file()
        if info is None:  # a magnet whose metadata has not resolved yet
            return []

        try:
            file_priorities = list(handle.get_file_priorities())
        except AttributeError:
            file_priorities = list(handle.file_priorities())

        storage = info.files()
        files = []
        for index in range(storage.num_files()):
            priority = file_priorities[index] if index < len(file_priorities) else 0
            files.append(
                pb.TorrentFileEntry(
                    index=index,
                    path=storage.file_path(index),
                    size=storage.file_size(index),
                    priority=int(priority),
                )
            )
        return files

    def create_test_torrent(self, size_bytes, name):
        # Both parameters come straight off the control port. The name decides a filename and the
        # size decides how much this process allocates, so neither is taken as given: the name is
        # reduced to one safe segment and the result re-confined, and the size is bounded because a
        # single request must not be able to exhaust the process that owns the torrent sockets.
        if size_bytes <= 0 or size_bytes > MAX_TEST_TORRENT_BYTES:
            raise ValueError(f"size_bytes must be between 1 and {MAX_TEST_TORRENT_BYTES}")

        seed_dir = tempfile.mkdtemp(prefix="seed_", dir=DATA_ROOT)
        file_path = os.path.join(seed_dir, paths.sanitize_name(name, fallback="test.bin"))
        if not paths.is_inside(DATA_ROOT, file_path):
            raise ValueError(paths.REJECTED)

        with open(file_path, "wb") as handle:
            handle.write(secrets.token_bytes(size_bytes))

        storage = lt.file_storage()
        lt.add_files(storage, file_path)
        torrent = lt.create_torrent(storage, piece_size=16 * 1024)
        lt.set_piece_hashes(torrent, seed_dir)
        entry = torrent.generate()
        torrent_bytes = lt.bencode(entry)

        params = lt.add_torrent_params()
        params.ti = lt.torrent_info(entry)
        params.save_path = seed_dir
        params.flags |= lt.torrent_flags.seed_mode
        seed_handle = self._seed_session.add_torrent(params)

        return bytes(torrent_bytes), _info_hash(seed_handle), self._seed_session.listen_port()

    def shutdown(self):
        self._stop.set()


class TorrentServicer(pb_grpc.TorrentServiceServicer):
    def __init__(self, engine, policy=netguard.POLICY_BLOCK):
        self._engine = engine
        self._policy = policy
        self._stream_slots = threading.BoundedSemaphore(STREAM_SLOTS)

    def AddTorrent(self, request, context):
        kind = request.WhichOneof("source")
        value = getattr(request, kind)
        if kind == "torrent_file" and len(value) > MAX_TORRENT_FILE_BYTES:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, "torrent file too large")
        try:
            info_hash, name, resumed = self._engine.add(kind, value, request.save_path, request.peers)
        except ValueError as error:
            # The message is the constant refusal, never the path that was asked for: echoing it back
            # would put somebody else's directory layout into a log an operator shares.
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(error))
        return pb.AddTorrentResponse(info_hash=info_hash, name=name, resumed=resumed)

    def GetStatus(self, request, context):
        status = self._engine.status(request.info_hash)
        if status is None:
            context.abort(grpc.StatusCode.NOT_FOUND, "unknown info_hash")
        return status

    def StreamStatus(self, request, context):
        # Refused rather than queued: a stream waiting for a slot would sit on a worker thread, which is
        # the very thing the slots exist to stop. The caller backs off and asks again.
        if not self._stream_slots.acquire(blocking=False):
            context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "too many status streams")
        try:
            ends_at = time.monotonic() + STREAM_LIFETIME_SECONDS
            while context.is_active():
                status = self._engine.status(request.info_hash)
                if status is None:
                    context.abort(grpc.StatusCode.NOT_FOUND, "unknown info_hash")
                yield status
                if status.is_finished or time.monotonic() >= ends_at:
                    return
                time.sleep(1.0)
        finally:
            self._stream_slots.release()

    def SaveResumeData(self, request, context):
        data = self._engine.save_resume(request.info_hash)
        if data is None:
            context.abort(grpc.StatusCode.NOT_FOUND, "no resume data")
        return pb.ResumeData(info_hash=request.info_hash, data=data)

    def RemoveTorrent(self, request, context):
        removed = self._engine.remove(request.info_hash, request.delete_files)
        return pb.RemoveTorrentResponse(removed=removed)

    def PauseTorrent(self, request, context):
        ok = self._engine.pause(request.info_hash)
        if not ok:
            context.abort(grpc.StatusCode.NOT_FOUND, "unknown info_hash")
        return pb.SimpleResponse(ok=True)

    def ResumeTorrent(self, request, context):
        ok = self._engine.resume(request.info_hash)
        if not ok:
            context.abort(grpc.StatusCode.NOT_FOUND, "unknown info_hash")
        return pb.SimpleResponse(ok=True)

    def SetFilePriorities(self, request, context):
        ok = self._engine.set_file_priorities(request.info_hash, request.priorities)
        if not ok:
            context.abort(grpc.StatusCode.NOT_FOUND, "unknown info_hash")
        return pb.SimpleResponse(ok=True)

    def ListFiles(self, request, context):
        files = self._engine.list_files(request.info_hash)
        if files is None:
            context.abort(grpc.StatusCode.NOT_FOUND, "unknown info_hash")
        return pb.FileList(info_hash=request.info_hash, files=files)

    def CreateTestTorrent(self, request, context):
        # Off unless the development topology turns it on: in production it is 64 MiB of random bytes
        # written per call by anyone who can reach the control port, and nothing there needs it.
        if not TEST_TORRENTS_ENABLED:
            context.abort(grpc.StatusCode.UNIMPLEMENTED, "test torrents are disabled")
        try:
            torrent_bytes, info_hash, seeder_port = self._engine.create_test_torrent(
                request.size_bytes, request.name
            )
        except ValueError as error:
            context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(error))
        return pb.CreateTestTorrentResponse(
            torrent_file=torrent_bytes, info_hash=info_hash, seeder_port=seeder_port
        )

    def OverrideTunnelHold(self, request, context):
        if not self._engine.override_hold():
            context.abort(
                grpc.StatusCode.FAILED_PRECONDITION,
                "the egress hold can be overridden only under the pause-and-alert policy")
        return pb.SimpleResponse(ok=True)

    def GetTunnelHealth(self, request, context):
        verdict, held = self._engine.tunnel_observation()
        return pb.TunnelObservation(
            tunnel_device=verdict.tunnel_device,
            tunnel_up=verdict.tunnel_up,
            default_route_via_tunnel=verdict.default_route_via_tunnel,
            egress_identity_matches=verdict.egress_identity_matches,
            reason=verdict.reason,
            policy=self._policy,
            session_held=held,
            observed_at_unix=verdict.checked_at,
        )


def _verify_writable_data_root():
    """Prove this account can write DATA_ROOT before anything is allowed to look healthy.

    The image creates and owns the data root, but Docker only applies image ownership to an *empty*
    volume: a volume written by an earlier build that ran as root stays root-owned. Without this check
    the port opens, the health probe reports healthy and every single download fails.
    """
    try:
        os.makedirs(DATA_ROOT, exist_ok=True)
        probe = os.path.join(DATA_ROOT, f".sidecar-write-probe-{os.getpid()}")
        with open(probe, "wb"):
            pass
        os.unlink(probe)
    except OSError as error:
        raise SystemExit(
            f"data root {DATA_ROOT} is not writable by uid {os.getuid()}: {error}. "
            "Check that the volume is mounted and owned by the account this container runs as."
        )


def _verify_tunnel_contract(device, policy):
    """Refuses to start when the configured policy cannot be honoured. Returns nothing; raises.

    Under `block` the tunnel is a precondition, so a missing device has to stop the process rather
    than produce a container that runs and quietly downloads over the household's connection. Under
    the two weaker modes the same condition is a startup warning: the operator chose to be told.
    """
    if not device:
        print(
            "no tunnel device configured (SIDECAR_TUNNEL_DEVICE is unset): torrent traffic leaves "
            "over this machine's own connection.",
            flush=True,
        )
        return

    verdict = _observe_safely(device)
    if verdict.healthy:
        print(f"tunnel egress verified on {device} (policy {policy})", flush=True)
        return

    if policy == netguard.POLICY_BLOCK:
        raise SystemExit(
            f"tunnel egress cannot be verified on device {device}: {verdict.reason}. "
            "SIDECAR_TUNNEL_POLICY is 'block', so this process will not start and no torrent "
            "traffic can leave. Bring the tunnel up, or choose a weaker policy knowingly."
        )

    print(
        f"WARNING: tunnel egress cannot be verified on device {device}: {verdict.reason}. "
        f"SIDECAR_TUNNEL_POLICY is '{policy}', which is weaker than 'block'; traffic is not "
        "guaranteed to stay inside the tunnel.",
        flush=True,
    )


def serve():
    _verify_writable_data_root()

    # An unset or misspelt policy normalises to the strongest one, so a typo cannot weaken an
    # installation. Which one is in force is printed below, whatever was configured.
    device = netguard.configured_device()
    policy = netguard.configured_policy()
    _verify_tunnel_contract(device, policy)

    token = control_auth.read_token()
    configuration_error = control_auth.validate_configuration(token, control_auth.token_required())
    if configuration_error:
        raise SystemExit(configuration_error)
    if not token:
        # Named, never valued. An open control port is a deployment choice and has to be visible in
        # the log of an installation that made it by accident.
        print(
            "WARNING: SIDECAR_CONTROL_TOKEN is not set. Anything that can reach the control port can "
            "drive the torrent engine.",
            flush=True,
        )

    engine = TorrentEngine(tunnel_device=device, tunnel_policy=policy)
    interceptor = control_auth.build_interceptor(token)
    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=STREAM_SLOTS + UNARY_WORKERS),
        interceptors=[interceptor] if interceptor else [],
        # One RPC per worker at most: with 32 MiB messages, an unbounded number of calls in flight is
        # an unbounded amount of memory held for them.
        maximum_concurrent_rpcs=STREAM_SLOTS + UNARY_WORKERS,
        options=[
            ("grpc.max_receive_message_length", MAX_MESSAGE_BYTES),
            ("grpc.max_send_message_length", MAX_MESSAGE_BYTES),
        ],
    )
    pb_grpc.add_TorrentServiceServicer_to_server(TorrentServicer(engine, policy), server)
    # add_insecure_port returns 0 when the address could not be bound, and the server would then start
    # serving nothing at all. A control port that is not listening must be a crash, not a silent no-op.
    if server.add_insecure_port(LISTEN_ADDRESS) == 0:
        raise SystemExit(f"cannot bind the control port to {LISTEN_ADDRESS}")
    server.start()
    print(
        f"torrent sidecar listening on {LISTEN_ADDRESS} (libtorrent {lt.version}, "
        f"tunnel {device or 'none'}, policy {policy}, control token {'set' if token else 'unset'}, "
        f"test torrents {'on' if TEST_TORRENTS_ENABLED else 'off'})",
        flush=True,
    )
    try:
        server.wait_for_termination()
    finally:
        engine.shutdown()


if __name__ == "__main__":
    serve()
