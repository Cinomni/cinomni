# Requirements

## The server

| | Supported | Notes |
|---|---|---|
| Architecture | `linux/amd64` | The only published platform. ARM is not supported. |
| Operating system | Any Linux that runs Docker | One machine. Running several instances is not supported. |
| Container runtime | Docker with the Compose plugin **v2.24 or newer** | Check with `docker compose version`. |
| CPU and memory | 4 CPUs and 2 GB for the application by default | Enough for 1080p. See below for 4K. |
| Database | PostgreSQL 16 | Included in `docker-compose.yml`; you do not install it. |

Windows and macOS can run the containers through Docker Desktop for trying Cinomni out, but that is
not a supported deployment, and Docker Desktop cannot pass a GPU to it (transcoding stays on the
CPU).

### Sizing for transcoding

Most household playback is Direct Play and costs almost nothing. What costs CPU is converting a
video the browser cannot play:

| Workload | What it needs |
|---|---|
| Direct Play, remux | Almost nothing. |
| One 1080p software transcode | One to two CPU cores. |
| 4K HDR on the CPU | 8–16 CPUs and about 6 GB of memory, or a GPU. The defaults are too slow for it. |
| Hardware transcoding | An Intel or AMD GPU (VAAPI / Quick Sync) or an NVIDIA GPU (NVENC). |

The limits are set in `.env` (`CINOMNI_CPUS`, `CINOMNI_MEMORY`). See
[Transcoding and hardware](../administration/transcoding.md).

## Storage

This is the requirement people most often get wrong.

- **Downloads and the library must be on the same filesystem.** Cinomni hardlinks a finished
  download into the library, so the file exists once on disk and can keep seeding. Across two
  filesystems a hardlink is impossible and every import becomes a full copy: double the space for as
  long as the download is kept.
- **Plan for the whole library plus the downloads still seeding**, and keep at least 5 GB free:
  below that, the health check reports the storage as degraded.
- **A NAS or external disk must be mounted before Cinomni starts.** Otherwise Cinomni sees an empty
  directory and starts a new, empty library on the wrong disk.

The default layout keeps everything under one host directory:

```text
data/
├── library/     your organised movies and series (back this up yourself)
└── downloads/   torrents while they download and seed
```

Transcoding scratch space and database backups live in separate Docker volumes.

## Accounts with third parties

None are required to start. Each one switches a feature on:

| Service | Needed for | Where you enter it |
|---|---|---|
| TMDB API key | Searching for movies, metadata and artwork | `.env` or **Console → Settings → Metadata** |
| TheTVDB API key (and PIN for a subscriber key) | Season and episode structure of series | `.env` or **Console → Settings → Metadata** |
| OpenSubtitles API key | Subtitles from OpenSubtitles | `.env` |
| SubDL API key | Subtitles from SubDL | the `Subtitles__Subdl__ApiKey` setting |
| Indexers | Finding releases | **Console → Indexers**, after setup |
| A VPN provider (optional) | Sending torrent traffic through a tunnel | `.env`, with the VPN overlay |

## Clients

Cinomni is used from a web browser: a current Chrome, Edge, Firefox or Safari, on a computer, tablet
or phone. The interface adapts to small screens. There are no native apps.

Which videos a browser can play without conversion depends on the browser and the device. Cinomni
asks the browser what it supports and converts only what it cannot play. See
[Watching](../user-guide/playback.md).
