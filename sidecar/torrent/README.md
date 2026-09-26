# Torrent sidecar (PoC)

Proof of concept for the libtorrent integration: libtorrent runs
as a **dedicated process** exposing a gRPC control surface, consumed by the .NET backend. A
crash in the native engine never takes down the backend, and the process runs inside the VPN
network namespace when an installation opts into one (see "The egress guard" below).

**Language sub-decision:** validated with **Python + python-libtorrent** (the Boost.Python
binding already wraps the full surface), which is the fastest way to de-risk the pattern.
Production may still choose a C++ daemon pinned to libtorrent 2.1 (`bc2df3c`); this PoC uses
the distro's `python3-libtorrent` (2.0.x), which is enough to prove the flow.

## What it validates

The critical flow of the integration — **add → progress → complete → restart-with-resume** — end
to end, deterministically and offline:

1. `CreateTestTorrent` generates a random file and seeds it from an internal session (no
   internet, no third-party content).
2. `AddTorrent` starts a download pointed at the local seeder.
3. `StreamStatus` streams progress until the download completes.
4. `SaveResumeData` returns the `.fastresume` checkpoint.
5. Re-adding from that resume data resumes without a full recheck (`resumed=true`).

## Contract

[`proto/torrent.proto`](proto/torrent.proto) — `TorrentService`:

- Core flow: `AddTorrent`, `GetStatus`, `StreamStatus` (the analogue of libtorrent's alert
  stream), `SaveResumeData`, `RemoveTorrent`.
- `StreamStatus` holds a worker thread while it is open, so streams get a fixed share of the pool
  (`STREAM_SLOTS`, with `UNARY_WORKERS` kept for every other call), a stream beyond that share is
  refused with `RESOURCE_EXHAUSTED`, and each one ends after `STREAM_LIFETIME_SECONDS`. The backend
  re-subscribes in turn, so a status stream can never starve the calls the kill-switch depends on.
- Re-adding from resume data keeps the blob's announce list after filtering it (http/https/udp only,
  bounded count and length, no userinfo, no `localhost` and no non-global address literal): DHT and
  local discovery are off, so without trackers a re-added torrent finds no peers and an unresolved
  magnet never resolves. The blob's web seeds are dropped, and `ssrf_mitigation` is set explicitly
  on the download session.
- Egress guard: `GetTunnelHealth` answers where this process's traffic would actually go, from a
  cached observation refreshed on a timer. It is the leak detector the backend polls.
- Control surface (consumed by the Downloads module): `PauseTorrent`/`ResumeTorrent`
  (manual pause clears `auto_managed` so the engine won't auto-resume; resume restores it), `SetFilePriorities`
  (0 = don't download .. 7 = top), `ListFiles` (file layout for per-file priorities).
- `TorrentStatus` also carries `all_time_upload`/`all_time_download`/`seeding_seconds` so the
  backend can enforce a seeding policy (libtorrent has no hard per-torrent ratio/seed-time cut).
- PoC-only: `CreateTestTorrent`, answered only when `SIDECAR_ENABLE_TEST_TORRENTS=1` (this file's
  compose sets it; no deployment file does). Elsewhere it is `UNIMPLEMENTED`.
- Every peer and tracker address in a private, loopback, link-local or multicast range is refused by
  an `ip_filter` on the download session. Loopback is let through only with test torrents on, for the
  internal seeder.

## Run it

```bash
# 1. build + start the sidecar (from the repo root)
docker compose -f docker-compose.sidecar.yml up -d --build

# 2. run the .NET client, which drives the full flow and prints each step
dotnet run --project poc/Cinomni.Torrent.Poc

# 3. stop it
docker compose -f docker-compose.sidecar.yml down
```

Expected tail: `== PoC OK ==`.

## The egress guard and the control port

Both are opt-in and both are off in this file. `docker-compose.vpn.yml` turns them on and
DEPLOYMENT.md section 7 explains the whole arrangement; what lives here is:

- `netguard.py` — the three observations (device address, default route, and the source address the
  kernel would pick) and the pure classification over them. `SIDECAR_TUNNEL_DEVICE` turns it on;
  `SIDECAR_TUNNEL_POLICY` (`block` by default) decides what a failed observation does. Under `block`
  the process refuses to start without its tunnel; under `block` and `pause-and-alert` it pauses the
  whole session when it loses it, and under `pause-and-alert` `OverrideTunnelHold` lifts that for the
  rest of the outage when an operator resumes a held download.
  A device missing for `SIDECAR_TUNNEL_ORPHAN_SECONDS` (120) ends the process, so a restart rejoins a
  tunnel container that came back in a new namespace;
- `server.py` binds the download session to that device — `listen_interfaces` **and**
  `outgoing_interfaces`, since either alone leaves a direction unbound. The internal PoC seeder stays
  on `127.0.0.1:0` so `CreateTestTorrent` still works offline;
- `paths.py` — every save path is confined to `SIDECAR_DATA`, and a `.fastresume` blob's embedded
  save path and tracker list are overwritten rather than trusted;
- `control_auth.py` — a shared-token interceptor on the control port, compared in constant time.
  Unset here, required by both deployment files.

Run the unit tests, which need no tunnel, no network and no privilege:

```bash
cd sidecar/torrent && python -m unittest discover -s tests
# or in the image's own environment: docker build --target test .
```

The suite runs in a build stage of its own and is not copied into the runtime image: the container
that parses hostile swarm data carries only what it executes.

## Not in scope (production work still to do)

- libtorrent 2.1 built from source (pin `bc2df3c`), not the 2.0.x distro package.
- Full alert coverage mapped to backend events; session metrics via `post_session_stats`.
- TLS on the gRPC channel. It carries a credential and never leaves the host; DEPLOYMENT.md
  section 7 records why that trade is the right one here and when it would stop being.
