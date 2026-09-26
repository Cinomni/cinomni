# Downloads

Cinomni downloads with its own BitTorrent engine, built on libtorrent, which runs in a separate
container (`torrent-sidecar`). It only sees the downloads folder: never your library, never the
recycle folder, never the database.

## Following a download

**Activity** lists every title Cinomni is acquiring. Open one to see:

- **Attempts**: each release that was tried, most recent first, and what became of it;
- the **download**: progress, speed, peers, and its own history;
- the **state history** of the acquisition as a whole.

From there you can **Pause**, **Resume** and **Retry** a download.

A download that fails does not give up on the title. Cinomni records why, and searches for another
release.

## Restarts

Download progress is saved in the database, not only on disk. After a restart (of Cinomni, of the
engine, or of the whole machine) downloads resume where they were, without starting again.

## Stalls and seeding

These are application settings, set in the `cinomni` service's `environment` in the compose file:

| Setting | Default | Meaning |
|---|---|---|
| `Downloads__Transfers__StallTimeout` | `1.00:00:00` (1 day) | How long a download may make no progress before it fails and another release is searched for. A paused or held download is waiting, not stalled. |
| `Downloads__Transfers__SeedRatioLimit` | `1.0` | Stop seeding once the upload reaches this multiple of the size. `none` removes the limit. |
| `Downloads__Transfers__SeedTimeLimit` | `7.00:00:00` (7 days) | Stop seeding after this long. `none` removes the limit. |

Seeding ends when the engine restarts: a finished download is closed rather than added again, because
re-adding it would download again whatever has been removed from the downloads folder since.

## Egress: direct or through a VPN

`CINOMNI_TORRENT_EGRESS` in `.env` records your choice, and has no default on purpose.

### Direct

The engine connects to trackers and peers over this machine's own internet connection. Your public
IP address is what every tracker and every peer sees. **Console → System → Torrent egress** says so.

### Through a VPN

With the VPN overlay, the engine runs inside a tunnel container's network and has no network of its
own:

```bash
docker compose -f docker-compose.yml -f docker-compose.vpn.yml up -d
```

Set `CINOMNI_TORRENT_EGRESS=vpn` and fill in the VPN block of `.env`: at least
`VPN_SERVICE_PROVIDER`, and the WireGuard or OpenVPN credentials your provider gives you. The default
tunnel image is gluetun, pinned to a known version; another image can be used if it meets the contract
described in [DEPLOYMENT.md §8](../../DEPLOYMENT.md#8-the-vpn-overlay-opt-in).

Only torrent traffic goes through the tunnel. Indexer searches, metadata and subtitles use the
server's normal connection.

Cinomni checks regularly that torrent traffic would really leave through the tunnel (the device
exists, the default route uses it, and the source address, in IPv4 and IPv6, is the tunnel's). When
that stops being true:

- the engine stops its own traffic immediately;
- every download is **held**: paused, with its progress kept, and marked *Network held*;
- administrators get a notification;
- nothing fails and nothing is searched for again.

When the tunnel is back and verified, held downloads resume on their own and another notification is
sent.

How strictly this is enforced is `CINOMNI_TUNNEL_LOSS_POLICY`:

| Policy | Behaviour |
|---|---|
| `block` (default) | Hold on the first failed check. A held download cannot be resumed by hand. |
| `pause-and-alert` | Hold after two failed checks in a row. **Resume anyway** is allowed, and recorded. |
| `ignore` | Only report. Traffic continues over whatever route exists. |

Before you trust it, run the drop drill in
[DEPLOYMENT.md §8](../../DEPLOYMENT.md#the-drop-drill) against your own tunnel.

## Using your own qBittorrent instead (opt-in)

Instead of the built-in engine, Cinomni can drive an existing qBittorrent through its Web API. Set
these in the `cinomni` service's `environment`:

| Setting | Meaning |
|---|---|
| `Downloads__Engine` | `Qbittorrent`. Unset, the built-in engine is used. |
| `Downloads__Qbittorrent__BaseAddress` | The Web UI address, for example `http://qbittorrent:8080`. |
| `Downloads__Qbittorrent__Username` / `__Password` | Its Web UI credentials (the username defaults to `admin`). |

qBittorrent must save into the same downloads folder, **at the same path**, that Cinomni imports from
(`/data/downloads` in the packaged layout), or finished downloads cannot be imported. Cinomni only
takes over torrents that save into that folder, never the ones you added to the client yourself.

qBittorrent is somebody else's process: Cinomni cannot see or enforce where its traffic goes. With a
VPN tunnel configured, the egress check therefore fails closed and downloads stay held; the
guarantees in the previous section, and the list below, apply only to the built-in engine. See
[SECURITY.md](../../SECURITY.md#external-processes-ffprobe-ffmpeg-the-sidecar).

## What the built-in engine will not do

- Connect to addresses on your own machine or local network, for peers or trackers. A tracker hosted
  on your LAN is unreachable.
- Use DHT, local peer discovery or UPnP/NAT-PMP port mapping.
- Accept commands without the shared `CINOMNI_SIDECAR_CONTROL_TOKEN`.
