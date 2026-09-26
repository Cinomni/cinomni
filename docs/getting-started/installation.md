# Installation

Cinomni runs as a set of containers described by one `docker-compose.yml`. Every image is published
on GitHub Container Registry (`ghcr.io/cinomni/...`) for each release, so nothing is compiled on your
machine.

Before you start, check the [requirements](requirements.md), especially the storage rules.

## 1. Get the compose files

Either download `docker-compose.yml` and `.env.example` from the
[latest release](https://github.com/Cinomni/cinomni/releases), or clone the repository:

```bash
git clone https://github.com/cinomni/cinomni.git
cd cinomni
```

A clone also gives you the optional overlays (`docker-compose.vpn.yml`, `docker-compose.hwaccel.yml`,
`docker-compose.nvidia.yml`).

## 2. Create `.env`

```bash
cp .env.example .env
```

Three values have no default, and `docker compose up` refuses to start until each one is set:

| Variable | What to put there |
|---|---|
| `POSTGRES_PASSWORD` | A long random password. Use only letters, digits and `-_.~`: a `'` or `$` breaks it. |
| `CINOMNI_TORRENT_EGRESS` | `direct` or `vpn`. This is a decision about your privacy; read the box below. |
| `CINOMNI_SIDECAR_CONTROL_TOKEN` | A random token protecting the torrent engine: `openssl rand -hex 32`. |

Strongly recommended as well:

| Variable | Why |
|---|---|
| `CINOMNI_SECRET_KEY` | Encrypts the credentials you store in Cinomni (indexer passwords and API keys, metadata keys). Without it Cinomni refuses to store any credential. Generate one with `openssl rand -base64 32` and keep it as safe as the database password: losing it makes every stored credential unreadable. |
| `TZ` | Your time zone, for example `Europe/Madrid`. |
| `TMDB_API_KEY` | Needed to search for movies. It can also be entered later in the console. |

> **Torrent traffic and your address.** With `CINOMNI_TORRENT_EGRESS=direct`, the torrent engine
> connects to trackers and peers over your own internet connection, and your public IP address is
> visible to everyone in the swarm. With `vpn`, you also apply the VPN overlay and the engine can
> only reach the internet through a tunnel; if the tunnel drops, downloads are held until it comes
> back. See [Downloads](../administration/downloads.md#egress-direct-or-through-a-vpn).

Every other variable is documented in `.env.example` itself and in
[DEPLOYMENT.md §3](../../DEPLOYMENT.md#3-settings).

## 3. Prepare the media directory

The containers run as user and group `1654`, never as root. The data directory must belong to that
account:

```bash
mkdir -p data/library data/downloads
sudo chown -R 1654:1654 data
```

If your media already lives elsewhere and belongs to you, point `CINOMNI_DATA_DIR` at it and set
`CINOMNI_UID` and `CINOMNI_GID` to your own ids (`id -u`, `id -g`) instead of changing ownership. The
directory must contain (or will get) a `library` and a `downloads` folder, **on the same
filesystem**.

## 4. Start it

```bash
docker compose pull
docker compose up -d
docker compose ps       # wait until cinomni reports "healthy"
```

The first start takes longer: the database schema is created before anything is served. The health
check allows two minutes for it.

By default Cinomni is only reachable from the machine itself, at `http://127.0.0.1:8080`. That is on
purpose: complete the [first run](first-run.md) before you make it reachable from anywhere else.

## Optional overlays

Overlays are extra compose files added with another `-f`. Use the same list of files for every
`docker compose` command afterwards.

| Overlay | Adds | Guide |
|---|---|---|
| `docker-compose.vpn.yml` | The torrent engine sealed inside a VPN tunnel | [Downloads](../administration/downloads.md) |
| `docker-compose.hwaccel.yml` | Intel/AMD GPU transcoding (VAAPI, Quick Sync) | [Transcoding](../administration/transcoding.md) |
| `docker-compose.nvidia.yml` | NVIDIA GPU transcoding (NVENC) | [Transcoding](../administration/transcoding.md) |
| `docker-compose.build.yml` | Build every image from the source checkout instead of pulling | [CONTRIBUTING.md](../../CONTRIBUTING.md) |

For example, with a VPN:

```bash
docker compose -f docker-compose.yml -f docker-compose.vpn.yml up -d
```

## Stopping and removing

```bash
docker compose down        # stop; everything is kept
docker compose down -v     # stop and DELETE the database, backup and transcode volumes
```

Your media under `data/` is never touched by either command.

## Next

[First run](first-run.md): create the administrator and connect your first sources.
