<div align="center">

<img src=".github/assets/logo.svg" alt="Cinomni logo" width="112" height="112">

# Cinomni

**Your movies and series, from discovery to playback, in one self-hosted app.**

[![CI](https://github.com/Cinomni/cinomni/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Cinomni/cinomni/actions/workflows/ci.yml)
[![Version](https://img.shields.io/badge/version-0.1.0--alpha.1-f5b544)](./CHANGELOG.md)
[![License: AGPL-3.0-or-later](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue)](./LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PostgreSQL 16](https://img.shields.io/badge/PostgreSQL-16-4169e1?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![React 18](https://img.shields.io/badge/React-18-61dafb?logo=react&logoColor=black)](https://react.dev/)
[![Docker](https://img.shields.io/badge/docker-linux%2Famd64-2496ed?logo=docker&logoColor=white)](./DEPLOYMENT.md)

[Website](https://cinomni.com) · [Quick start](#quick-start-docker) · [Deployment](./DEPLOYMENT.md) ·
[Roadmap](./ROADMAP.md) · [Changelog](./CHANGELOG.md)

</div>

---

Cinomni is a self-hosted media platform: it organizes your library, finds releases through the
indexers **you** configure, fetches them with a built-in BitTorrent client, imports and renames them,
adds subtitles, and streams them to your devices, all in one **modular monolith** (.NET 10 +
PostgreSQL 16) with a React + TypeScript web client.

> **Status: alpha, current version `0.1.0-alpha.2`.** Movies and series work end to end (catalog,
> monitoring, discovery, decision, downloads, import, library, subtitles and playback), with
> requests, notifications, an operator console and two-factor sign-in on top. An alpha has known gaps
> and no upgrade history yet: read the release notes in **[CHANGELOG.md](./CHANGELOG.md)** before
> running it, and **[ROADMAP.md](./ROADMAP.md)** for the road to beta and to a stable 1.0.0.

## Features

- **Library and catalog**: movies and series with seasons and episodes, artwork and metadata from
  TMDB/TheTVDB, genres, collections, and an optional per-account content ceiling.
- **Automation you can read**: monitoring policies, release parsing and scoring profiles; every
  grab and every rejection is explained, not just logged.
- **Your own indexers**: Torznab/Newznab-style and declarative definitions, including private
  trackers that sign in and sit behind a Cloudflare challenge.
- **Built-in downloads**: a libtorrent sidecar that fails closed when the optional VPN tunnel is down,
  with recoverable, resumable transfers.
- **Import that keeps your disk sane**: hardlinks when the storage allows it, renaming, season packs,
  upgrades that recycle the file they replace.
- **Subtitles**: fetched per wanted language, re-searched when none is found yet, and shown in the
  player alongside the file's own tracks.
- **Playback in the browser**: Direct Play when the device can, otherwise remux or transcode to HLS,
  and the reason is shown. The player has its own controls, keyboard shortcuts, and in-player choice
  of audio track, subtitles (text, or picture subtitles burned in), quality and speed; it resumes
  where you left off and remembers your tracks per title.
- **Transcoding that fits the machine**: hardware encoders are verified with a real test encode and
  decode (VAAPI, Quick Sync, NVENC; AMF on a native Windows host), with HDR/Dolby Vision tone mapping,
  HEVC output, server-wide resolution and bitrate ceilings, and every knob live in the console.
- **Household-ready**: accounts and roles, requests with approval and per-account limits,
  notifications, backups, health checks and OpenTelemetry metrics.

## Quick start (Docker)

Cinomni ships as one `linux/amd64` image carrying the backend, the web client and FFmpeg, with a
production compose file beside it:

```bash
cp .env.example .env      # set POSTGRES_PASSWORD and CINOMNI_TORRENT_EGRESS at minimum
docker compose pull
docker compose up -d
```

The images are published on GitHub Container Registry for every release, and `docker-compose.yml`
and `.env.example` are attached to each release on GitHub. To build from source instead, add
`docker-compose.build.yml`.

Then open `http://127.0.0.1:8080` (see `CINOMNI_BIND_ADDRESS` / `CINOMNI_HTTP_PORT`) and complete
the first-run setup, which creates the administrator. Do it before the address is reachable from
anywhere else. Add your indexers, metadata keys and subtitle provider from the console afterwards.

Read **[DEPLOYMENT.md](./DEPLOYMENT.md)** before putting real media behind it, especially the
storage contract (it decides whether an import hardlinks or silently copies your whole library) and
the egress mode (it decides whether torrent traffic leaves over your own connection).

### Hardware transcoding

| Host | GPU | How |
|---|---|---|
| Linux | AMD or Intel | VAAPI (or Quick Sync on Intel) with `docker-compose.hwaccel.yml` |
| Linux | NVIDIA | NVENC with `docker-compose.nvidia.yml` and the NVIDIA Container Toolkit |
| Windows, native | AMD / Intel / NVIDIA | AMF / Quick Sync / NVENC (outside the supported MVP deployment) |
| Windows, Docker Desktop | any | Software only: the GPU is not passed to Linux containers |

Console → Settings → Playback shows what passed the hardware test and lets you run it again. CPU-only
4K/HDR transcoding needs more than the default `CINOMNI_CPUS=4.0` / `CINOMNI_MEMORY=2g`. Details,
every transcoding setting, and the native Windows setup are in section 9 of
**[DEPLOYMENT.md](./DEPLOYMENT.md)**.

## Development

Requirements: **.NET 10 SDK**, **Node.js 22** and **Docker**.

```bash
# PostgreSQL for development (host port 5442)
docker compose -f docker-compose.dev.yml up -d

# Backend: build, test, run (API on http://localhost:5268)
dotnet tool restore
dotnet build src/Cinomni.slnx
dotnet test  src/Cinomni.slnx
dotnet run --project src/Host/Cinomni.Host

# Web client (http://localhost:5173, proxies /api and /health to the API)
cd web
npm ci
npm run dev
npm run typecheck && npm run lint && npm run test && npm run build

# (Optional) libtorrent sidecar for the downloads module
docker compose -f docker-compose.sidecar.yml up -d --build
```

The development connection string targets `Database=cinomni`; the development user and credentials
live in `docker-compose.dev.yml` and are **never** for production. EF Core design-time tooling reads
the `CINOMNI_DB` environment variable when present. Integration tests use real PostgreSQL. Run test
projects one at a time, as the full suite in parallel exhausts its connections.

## Architecture

Each module owns its own PostgreSQL schema and exposes only its `*.Contracts` project (interfaces
and domain events); other modules depend on the contracts, never on the implementation. The `Host`
is the composition root that wires everything together, and modules talk through interfaces or
events over a transactional outbox. See [`src/README.md`](./src/README.md) for the module pattern
and the dependency rules.

```text
src/       .NET solution: Platform, Modules/<Name>, Host
tests/     xUnit unit and PostgreSQL integration tests
web/       React + Vite SPA
sidecar/   isolated Python/libtorrent gRPC process
```

## Documentation

| Document | For |
|---|---|
| [docs/](./docs/README.md) | User and administrator guides: installation, first run, the library, playback, the console, troubleshooting |
| [DEPLOYMENT.md](./DEPLOYMENT.md) | Running it on a server: storage, egress/VPN, backups, transcoding and GPUs |
| [CONTRIBUTING.md](./CONTRIBUTING.md) | Architecture, the module pattern, building and testing |
| [SECURITY.md](./SECURITY.md) | Security practices and reporting a vulnerability |
| [ROADMAP.md](./ROADMAP.md) | What is shipped, what is next, and what is out of scope |
| [CHANGELOG.md](./CHANGELOG.md) | What changed, for people who run Cinomni |

## Acceptable use

Cinomni does not host, provide, or index any content; you bring your own sources. Use it only with
content you have the legal right to download and store. You are responsible for complying with the
applicable laws in your jurisdiction and the terms of the services you connect to.

## License

Cinomni is licensed under the **GNU Affero General Public License v3.0 or later**
(`AGPL-3.0-or-later`). See [LICENSE](./LICENSE) for the full terms.
