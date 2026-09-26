# Cinomni as one image: the ASP.NET Core Host, the built web client in wwwroot, and the FFmpeg tools the
# Import and Playback modules launch as child processes. One platform — linux/amd64 — per MVP scope;
# build it with `docker build --platform linux/amd64 --target runtime -t cinomni .` from the repository
# root. `--target runtime` is not optional: the last stage in this file is `runtime-hwaccel` (opt-in,
# GPU driver packages — see docker-compose.hwaccel.yml), and Docker builds the last stage by default
# when none is named.
#
# Every base image is pinned by digest so a rebuild is reproducible. DEPLOYMENT.md records each image's
# provenance and licence and how to refresh a pin.

# ---------------------------------------------------------------------------
# Stage 1 — the web client. `tsc -b && vite build` emits web/dist.
# ---------------------------------------------------------------------------
FROM node:22-bookworm-slim@sha256:6c74791e557ce11fc957704f6d4fe134a7bc8d6f5ca4403205b2966bd488f6b3 AS web

WORKDIR /src/web

# Manifests first: a source-only change then reuses the installed dependency layer.
COPY web/package.json web/package-lock.json ./
RUN npm ci

COPY web/ ./
# Point every screen (including sign-in and playback) to this image's corresponding source.
# Set this to the exact, accessible source revision when distributing a modified build.
ARG CINOMNI_SOURCE_URL=
ENV VITE_SOURCE_URL=${CINOMNI_SOURCE_URL}
RUN npm run build

# ---------------------------------------------------------------------------
# Stage 2 — the backend. Publishes the composition root and everything it references.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build

WORKDIR /src

# Build identity, reported by GET /api/system/info. All three are optional: an unstamped build is a
# valid one that answers with its compiled version and nulls for the rest, which is what every
# `dotnet build` on a contributor's machine already is. See DEPLOYMENT.md for the release recipe.
# CINOMNI_VERSION is empty by default so an unstamped build reports the version the source declares
# (src/Directory.Build.props) instead of a made-up one.
ARG CINOMNI_VERSION=
ARG CINOMNI_COMMIT=
ARG CINOMNI_BUILD_DATE=

COPY src/ ./src/
# The Downloads gRPC client compiles the sidecar's proto file; without it the solution does not build.
COPY sidecar/torrent/proto/ ./sidecar/torrent/proto/

RUN dotnet restore src/Host/Cinomni.Host/Cinomni.Host.csproj

# No apphost: the entry point is `dotnet Cinomni.Host.dll`, so a native launcher would be dead weight.
# Every package the modules reference is fully managed and InvariantGlobalization is on, so the
# framework-dependent, RID-agnostic publish carries no native payload and needs no ICU.
RUN dotnet publish src/Host/Cinomni.Host/Cinomni.Host.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -p:UseAppHost=false \
        ${CINOMNI_VERSION:+"-p:Version=${CINOMNI_VERSION}"} \
        -p:SourceRevisionId="${CINOMNI_COMMIT}" \
        -p:BuildDate="${CINOMNI_BUILD_DATE}"

# ---------------------------------------------------------------------------
# Stage 3 — the runtime.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS runtime

# ffmpeg carries both ffmpeg and ffprobe — the two binaries Playback and Import launch by argv, and the
# reason the application cannot run on a bare runtime image. curl exists only so the HEALTHCHECK below
# has something to probe with: the runtime image ships no HTTP client at all.
#
# postgresql-client-16 carries pg_dump, pg_restore and psql. It ships here rather than being the
# operator's problem because a backup this image cannot take is a backup nobody has: the scheduled job
# runs inside this container, and an installation whose only copy of its accounts, library and decisions
# lives in one database volume must be able to write that copy without anything else being installed.
# The major version is pinned to the server's on purpose — a client may dump an older server but a
# restore needs the two to match, so this line moves when docker-compose.yml's postgres image does.
# About 4.8 MB installed, and DEPLOYMENT.md records its provenance and licence.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ffmpeg curl postgresql-client-16 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish ./
COPY LICENSE ./LICENSE
# UseStaticFiles reads this directory at runtime, which is why the bundle can land after the publish.
COPY --from=web /src/web/dist ./wwwroot
# The restore script runs HERE and not on the host: the database publishes no port and sits on an
# internal network, so this container is the only place that can reach it — and the only place with
# pg_restore, psql, sha256sum and curl. Shipping it means the documented procedure needs no bind mount
# and no client installed on the host. DEPLOYMENT.md, section 6, has the command.
COPY scripts/cinomni-restore.sh ./scripts/cinomni-restore.sh

# The three storage roots. In a real deployment they are mount points; creating and owning them here
# means an unmounted installation still starts and the startup check can prove writability.
# APP_UID is the non-root account the base image already provides — Cinomni invents no new identity,
# and the sidecar image is aligned to the same uid so hardlinked imports do not degrade into copies.
#
# Only /data changes hands. /app stays owned by root and is read-only to the runtime account: the process
# needs to read its assemblies and the web bundle, never to rewrite them. That matters more since the
# bundle became same-origin with the API — a writable /app/wwwroot would turn any file-write primitive
# into persistent JavaScript running with the operator's session.
#
# /data/backups is the fourth. It holds database dumps, which are credential stores — delivery-channel
# URLs, indexer addresses, password verifiers, session token hashes — so it is created 0700 and the
# files inside are written 0600. docker-compose.yml puts it on a volume of its own so it is not mixed
# into whatever the media directory is shared with.
RUN mkdir -p /data/library /data/downloads /data/transcodes /data/backups \
    && chown -R "$APP_UID:$APP_UID" /data \
    && chmod 700 /data/backups

USER $APP_UID

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
VOLUME ["/data"]

# Liveness, deliberately, and not readiness. A container health probe is what an orchestrator restarts
# on, and /health/ready goes 503 while PostgreSQL is restarting or failing over — a fault no restart can
# fix, and one a Swarm deployment or an autoheal sidecar would answer by killing this container in a
# loop. /health/live runs no check at all: it says the process is listening, which is the only question
# a restart is the answer to. Readiness stays available on /health and /health/ready for a load balancer
# or a human to ask.
# The start period has to cover a cold first boot, which applies every module's migrations before it
# serves anything — measure it on your own hardware and raise it if the first start is reaped.
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Cinomni.Host.dll"]

# ---------------------------------------------------------------------------
# Stage 4 — the hwaccel runtime variant. Built only by docker-compose.hwaccel.yml, never by default.
# ---------------------------------------------------------------------------
# Adds the VAAPI/QSV userspace drivers on top of the same runtime image. The base `runtime` target
# above stays free of GPU-vendor packages — the same "opt-in, never baked into the base image" rule
# the VPN overlay follows for gluetun (section 8). NVENC needs no package here at all:
# nvidia-container-toolkit injects the proprietary driver libraries into the container at start, and
# ffmpeg's own h264_nvenc support only ever needed the BSD-licensed, header-only ffnvcodec headers
# already present when the base image's ffmpeg was built — see docker-compose.nvidia.yml.
FROM runtime AS runtime-hwaccel

USER root

# mesa-va-drivers: the open VAAPI backend for AMD GPUs and older Intel iGPUs (the i965 driver).
# intel-media-va-driver-non-free: Intel's own iHD VAAPI driver for Broadwell (2014) and newer, and the
# backend ffmpeg's h264_qsv path uses on Linux today. "non-free" is Ubuntu's component name for its
# redistribution terms, not a licence Cinomni's own code takes on — DEPLOYMENT.md section 12 records
# it once a real build has read the package's own licence file.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends mesa-va-drivers intel-media-va-driver-non-free \
    && rm -rf /var/lib/apt/lists/*

USER $APP_UID
