# Deploying Cinomni

This document is for the person who runs Cinomni on their own machine. It is self-contained: it
describes the image, the settings it reads, the one storage rule that is not optional, and what to do
when something is wrong.

For development (building, testing, the module pattern), read [CONTRIBUTING.md](./CONTRIBUTING.md)
instead. For security practices and how to report a vulnerability, read
[SECURITY.md](./SECURITY.md).

---

## 1. What you are deploying

One image, built from the repository root by [`Dockerfile`](./Dockerfile):

| | |
|---|---|
| Platform | `linux/amd64` only. One node. |
| Contains | The ASP.NET Core Host (every module, in one process), the built web client in `/app/wwwroot`, `ffmpeg` / `ffprobe`, the PostgreSQL 16 client so the installation can back up *and restore* its own database, and the restore script itself at `/app/scripts/cinomni-restore.sh` (section 6). |
| Runs as | uid/gid **1654**, the non-root account the .NET base image provides. Never root. Every capability is dropped, and the container filesystem is read-only apart from `/data` and a temporary filesystem for scratch, so the application never rewrites its own code or the bundle it serves. |
| Listens on | port **8080**, plain HTTP. There is no TLS inside the image. |
| Serves | `/api/**` (the API), `/health` (the probe), and every other path from `index.html` so a deep link survives a reload. |
| Storage | `/data/library`, `/data/downloads`, `/data/transcodes`, `/data/backups`. |
| Database | PostgreSQL 16, a separate container. Cinomni applies every schema migration itself, on startup. |

The libtorrent sidecar is a second image, built from `sidecar/torrent/`. It runs as the same uid
(section 4 explains why that matters), it is never published on the host, and it is given the download
staging directory only, not your library.

FlareSolverr 3.4.5 is an isolated browser service at `http://flaresolverr:8191` on the internal
`indexer-control` network. It has no host port and no internet-capable network. Its explicit
`PROXY_URL` points to `indexer-egress`, the only service on both `indexer-control` and `edge`.
That forward proxy resolves names itself, rejects every DNS answer unless all answers are public, and
connects to the validated numeric address rather than resolving the name again. It accepts HTTP proxy
requests and HTTPS CONNECT, but never logs target hosts, paths, queries, or URLs. Cinomni's derived
image keeps the upstream image pinned by digest and adds Chrome's `--proxy-bypass-list=<-loopback>`;
without that flag Chrome can silently bypass a configured proxy for loopback and link-local targets.
FlareSolverr runs at warning log level with bounded CPU, memory, processes and application concurrency.

`docker-compose.yml` wires the three together and needs nothing else: no overlay, no tunnel.
`docker-compose.dev.yml` and `docker-compose.sidecar.yml` are development files: they are not used
here, and the credentials in them are development-only and must never be reused.

> ### Read this before you acquire anything
>
> **In the default topology the torrent sidecar talks to trackers and peers over this machine's own
> internet connection.** Your household's public address is what the swarm sees and what a tracker
> records for every torrent you take part in. Cinomni does not hide it, and there is nothing here that
> stops traffic if a tunnel goes down.
>
> That is why `CINOMNI_TORRENT_EGRESS` has no default: `docker compose up` refuses to start until you
> write down which of the two you are choosing.
>
> | `CINOMNI_TORRENT_EGRESS` | What you get |
> |---|---|
> | `direct` | The above. Everything works; nothing is hidden. |
> | `vpn` | You also apply `docker-compose.vpn.yml` (section 8) with a tunnel image you chose and trust. The sidecar runs inside a tunnel container's network namespace with no addresses of its own, libtorrent is bound to the tunnel device by name, and Cinomni holds every download the moment its traffic stops verifiably leaving through that tunnel. Downloads keep their progress and resume on their own. |
>
> The value is recorded on the sidecar container and is not enforced. It exists so that nobody starts
> downloading without having made the choice. What enforces anything is the overlay, and only when it
> is applied.

---

## 2. Before the first run

You need Docker with the Compose plugin, **v2.24 or newer** (`docker compose version`). The VPN overlay
in section 8 uses a merge directive older versions do not understand. And you need a directory for your
media.

```bash
git clone https://github.com/cinomni/cinomni.git
cd cinomni

cp .env.example .env
# Open .env and set POSTGRES_PASSWORD and CINOMNI_TORRENT_EGRESS.
# Nothing starts until both are set — see the box in section 1 for what the second one means.
```

Then create the media directory and give it to the account the containers run as:

```bash
mkdir -p data/library data/downloads
sudo chown -R 1654:1654 data
```

If your media already lives somewhere and is owned by you, point `CINOMNI_DATA_DIR` at it and set
`CINOMNI_UID` / `CINOMNI_GID` to your own ids (`id -u`, `id -g`) instead of chowning anything.

If you do that **after having started Cinomni once**, also remove the transcode volume, which was
created for the previous account and would otherwise stay unwritable:

```bash
docker compose down
docker volume rm cinomni_cinomni-transcodes
```

Cinomni will not refuse to start over it (an unwritable transcode directory is a logged warning and
costs only just-in-time transcoding), but transcodes will fail until the volume is recreated. The same
applies to the sidecar's development volume if you ever used `docker-compose.sidecar.yml` before this
version: `docker volume rm cinomni-torrent-data`, or give it to the new account once with
`docker run --rm -u 0:0 -v cinomni-torrent-data:/data ghcr.io/cinomni/cinomni-torrent-sidecar:<version> chown -R 1654:1654 /data`.
Unlike the transcode volume, the sidecar refuses to start when it cannot write its data root, and says
so in its log.

---

## 3. Settings

Everything comes from `.env`, which `docker-compose.yml` reads. `.env` is git-ignored; `.env.example`
is the committed template and holds no real values.

### Required

| Variable | What happens without it |
|---|---|
| `POSTGRES_PASSWORD` | `docker compose up` aborts. There is no default and there will never be one. Two characters cannot be used: `'`, which ends the quoted password inside the connection string, and `$`, which Compose expands while reading `.env`, so the value that arrives is not the one you pasted. Letters, digits and `-_.~` are always safe. |
| `CINOMNI_TORRENT_EGRESS` | `docker compose up` aborts. `direct` or `vpn`; read the box in section 1. There is no default because there is no answer Cinomni can pick for you. |
| `CINOMNI_SIDECAR_CONTROL_TOKEN` | `docker compose up` aborts. The credential on the torrent sidecar's gRPC control port, in either topology (section 8, "The control port"). `openssl rand -hex 32`. |

### Secret storage

| Variable | Default | Meaning |
|---|---|---|
| `CINOMNI_SECRET_KEY` | *(empty)* | The key stored secrets are encrypted with: today, an indexer's credential (its API key, or the password for a definition-backed login) and a metadata provider key entered under Settings. 32 bytes, base64: `openssl rand -base64 32`. |

Empty is a supported state, not a broken one: the installation starts, logs once that the variable is
unset, and **refuses to store a credential** rather than saving one in plaintext. The attempt fails
naming this variable. Everything that needs no credential (a public indexer, the whole rest of the
application) is unaffected.

Treat it like the database password, and note what losing it costs. It does not lose the
installation; it makes every secret stored under it unreadable, and Cinomni reports those as *not
configured* rather than pretending to decrypt them. Changing the key does the same thing, so rotate
deliberately and re-enter what it protected. Nothing here can re-encrypt what it can no longer read.

### Storage and identity

| Variable | Default | Meaning |
|---|---|---|
| `CINOMNI_DATA_DIR` | `./data` | Host directory mounted at `/data` in **both** containers. Read section 4. |
| `CINOMNI_UID` / `CINOMNI_GID` | `1654` | The account both containers run as. It must own `CINOMNI_DATA_DIR`. |
| `TZ` | `UTC` | Time zone for timestamps in the interface and the logs. |

### Downloads

Application settings rather than `.env` variables: set them in the `cinomni` service's `environment`
(for example `Downloads__Transfers__StallTimeout: "12:00:00"`).

| Setting | Default | Meaning |
|---|---|---|
| `Downloads__Transfers__StallTimeout` | `1.00:00:00` | How long a download may make no progress while the engine is trying before it fails and its goal searches for another release. Paused, held and engine-queued downloads are waiting, not stalled. |
| `Downloads__Transfers__SeedRatioLimit` | `1.0` | A new download stops seeding once it has uploaded this multiple of what it downloaded. Empty or `none` removes the bound. |
| `Downloads__Transfers__SeedTimeLimit` | `7.00:00:00` | A new download stops seeding after this long. Empty or `none` removes the bound. |

Seeding lasts as long as the sidecar holds the torrent. A sidecar restart ends it: the finished
download is closed on its history (`SeedingLost`) rather than added again, because re-adding would
fetch again whatever has since been deleted from staging.

The sidecar never connects to an address on this machine or its networks (private, loopback,
link-local, CGNAT and multicast ranges, in IPv4 and IPv6) for peers and trackers alike. A tracker
hosted on your own network is therefore unreachable from Cinomni.

### Network

| Variable | Default | Meaning |
|---|---|---|
| `CINOMNI_BIND_ADDRESS` | `127.0.0.1` | Loopback by default, because the image speaks plain HTTP. Widen it only behind a reverse proxy. |
| `CINOMNI_HTTP_PORT` | `8080` | Published port on the host. |

The browser boundary publishes no ports. Cinomni reaches FlareSolverr by the stable Compose service
name `flaresolverr`; FlareSolverr has no ambient `HTTP_PROXY` or `HTTPS_PROXY`, only its application
specific `PROXY_URL=http://indexer-egress:8080`. Do not attach FlareSolverr directly to `edge` or add
other services to `indexer-control`: either change removes the deployment boundary's useful isolation.

### Publishing beyond loopback

`CINOMNI_BIND_ADDRESS` defaults to `127.0.0.1` because the image speaks plain HTTP and carries none of
the defences a public endpoint needs. Widening it puts an unprotected application on the network, so
widen it **only** behind a reverse proxy, and only one that does all four of the following. None of
them is optional, and none of them is something Cinomni will do for you. See *What an internet-facing
deployment does not get from Cinomni* in [SECURITY.md](./SECURITY.md).

1. **Terminate TLS**, and redirect plain HTTP to it. Credentials and the session bearer token cross
   the network on every request.
2. **Declare the proxy, and make it write `X-Forwarded-For` itself.** Cinomni throttles
   `POST /api/identity/login` and `POST /api/identity/setup` (ten requests a minute per client by
   default, adjustable under **Console → Settings**), but it counts the address the request *arrives
   from*. Behind a proxy you have not declared, that address is the proxy for everybody: the whole
   household lands in one bucket and locks each other out, while the attacker sharing it is no more
   restricted than before. Two things are needed, and **both** of them:

   - Set `CINOMNI_TRUSTED_PROXIES` to your proxy's address or its CIDR range (comma-separated for
     several; a container's address is assigned by the runtime, so the range is usually the stabler
     choice). Set `CINOMNI_PROXY_HOPS` if more than one proxy is chained; it counts *hops*, not
     addresses. A proxy left out is simply not believed, which fails safe.
   - **Configure the proxy to set the header, not to relay one.** nginx:
     `proxy_set_header X-Forwarded-For $remote_addr;` (or `$proxy_add_x_forwarded_for` to append to a
     chain you control). A bare `proxy_pass` forwards the client's own headers untouched, and then a
     caller picks their own rate-limit bucket by typing a different `X-Forwarded-For` on each
     request. That is a complete bypass that looks identical from Cinomni's side, because from here a
     relayed header and one the proxy wrote are the same bytes. Caddy and Traefik set it correctly by
     default; nginx does not.

   Cinomni logs at startup which address it is counting and how many proxies it trusts, so you can
   confirm the result rather than assume it.
3. **Keep the session token out of your access log.** The web client appends the bearer token as a
   query parameter for media elements, which cannot set headers. Anything logging full request URLs
   will record those tokens; exclude the query string, or the log becomes a credential store.
4. **Forward the scheme, and set `CINOMNI_HSTS=true`.** Cinomni sets its own content security
   policy and hardening headers on every response, so there is nothing for the proxy to add. HSTS is
   the exception and it needs both halves: the switch, because it is a statement about your
   deployment that no request can establish, and `proxy_set_header X-Forwarded-Proto $scheme;` so
   Cinomni can tell which requests actually arrived over TLS.

   Do not set the switch unless TLS really is in front. `Strict-Transport-Security` tells a browser
   to refuse plain HTTP to this host for six months, and the only way back is clearing HSTS state in
   every browser that saw it. Leaving it off costs nothing but a header: everything that protects
   the session token is in the content security policy, which is always sent.

   If your proxy sets its own copy of any of these headers it wins, which is the right way round; a
   proxy that strips them leaves the client with none.
5. **Complete first-run setup before the address is reachable.** `POST /api/identity/setup` has to be
   anonymous (there is no account yet to authorize it), and it hands the administrator account to
   whoever calls it first. Create yours over loopback, then publish.

A single-user installation reached over a VPN or a private network needs none of this, which is the
deployment this default is aimed at.

### Database

| Variable | Default | Meaning |
|---|---|---|
| `POSTGRES_USER` | `cinomni` | Change only before the first start; the data volume is created for whatever this says. |
| `POSTGRES_DB` | `cinomni` | Same. |

### VPN: only read when you apply the overlay

Nothing here is read without `docker-compose.vpn.yml`. Section 8 explains what they do; this is the
list.

| Variable | Default | Meaning |
|---|---|---|
| `VPN_SERVICE_PROVIDER` | **none** | Required by the overlay. Which provider the tunnel image connects to. |
| `VPN_TYPE` | `wireguard` | `wireguard` or `openvpn`. |
| `WIREGUARD_PRIVATE_KEY`, `WIREGUARD_ADDRESSES`, `WIREGUARD_PRESHARED_KEY` | empty | WireGuard credentials, passed to the tunnel image unread. Cinomni never parses or logs them. |
| `OPENVPN_USER`, `OPENVPN_PASSWORD` | empty | The OpenVPN equivalent. |
| `VPN_SERVER_COUNTRIES`, `VPN_SERVER_CITIES` | empty | Where the provider should put you. |
| `CINOMNI_TUNNEL_LOSS_POLICY` | `block` | `block`, `pause-and-alert` or `ignore`. Read by both the sidecar and the application, which accept the same three words in any casing. A value that is not one of the three becomes `block`. |
| `CINOMNI_TUNNEL_DEVICE` | `tun0` | The interface the tunnel creates, libtorrent binds to and Cinomni verifies: one name for all three. |
| `CINOMNI_TUNNEL_POLL_INTERVAL` | `00:00:30` | How often the application re-checks. The sidecar stops its own traffic on the first failed observation regardless. |
| `CINOMNI_VPN_IMAGE` | pinned gluetun | A different tunnel image. Section 7 states the contract it has to satisfy. |
| `CINOMNI_SIDECAR_LISTEN` | `0.0.0.0:50051` | Where the control port binds inside the tunnel namespace. |
| `CINOMNI_VPN_OUTBOUND_SUBNETS` | empty | Only if Cinomni's own network is on a subnet the tunnel container does not treat as local. |

### Resource limits

`CINOMNI_CPUS` (4.0), `CINOMNI_MEMORY` (2g), `SIDECAR_CPUS` (2.0), `SIDECAR_MEMORY` (1g). These are
Compose v2 `deploy.resources.limits`; older tooling ignores them silently. The application defaults
are sized for 1080p: converting 4K HDR on the CPU needs more of both (8–16 CPUs and `6g`), or a
hardware backend (section 9, "HDR and Dolby Vision").

`CINOMNI_MAX_TRANSCODES` (4) and `CINOMNI_MAX_TRANSCODES_PER_ACCOUNT` (2) bound how many streams are
converted at once: transcodes and remuxes alike, since each is an FFmpeg process and a converted copy
on the transcode volume. A request past either limit is refused with a message that says which one,
and the player shows it; direct play is never limited. Software-transcoding a 1080p film takes one to
two cores, so without a hardware overlay (section 9) and on the default `CINOMNI_CPUS`, 2 is the
honest node limit. `cinomni.transcode.refused` counts the refusals: a steady trickle of them in the
evenings means the limit is lower than the household needs.

A conversion ends when its viewer stops, and also when nothing has asked for it for five minutes
(`Playback__TranscodeIdleTimeout`, two minutes at the least): a closed tab never says goodbye. Its
FFmpeg process is stopped and its output removed at that moment, not when the session row ages out.
A conversion that fails part way ends the same way, and the reason (FFmpeg's last words) is on the
transcode job in the database, never in the log.

`Playback__TranscodeRoot` must be a directory of its own: Cinomni deletes in it without being asked,
so startup refuses a transcode root that contains, or sits inside, the library or the staging area.
Those FFmpeg processes belong to the application process; in the packaged image that is the
container's PID 1, so they die with it. Run any other way, a crash can leave one behind, and the
next start stops it, having recorded which process it was. It checks the id, the start time, the
binary and, on Linux, the process's own command line before it does, so a reused id is never
mistaken for it.

No stream holds its slot for ever: one that reaches `Playback__MaxTranscodeLifetime` (6 hours, one
at the least) is ended however busy its player keeps it, and a viewer who loses access to the title
has the stream closed at their next progress report. Every session records why it ended (watched,
stopped, idle, expired, access revoked, failed, restarted) on the session row, and
`GET /api/playback/sessions/{id}` returns it as `endReason`.

### Provider credentials: all optional

Each one is optional, and each disables something precisely. Cinomni starts either way and logs the
**name** of every key it did not find. It never logs a value.

The metadata keys (`TMDB_API_KEY`, `TVDB_API_KEY`, `TVDB_PIN`) can also be entered by an administrator
under **Settings > Metadata**. They are stored encrypted with `CINOMNI_SECRET_KEY`, which must be set for
that, and apply at once, without a restart. A non-empty environment variable wins over a stored key; an
empty one does not. A provider with no key says so in the log the first time it is needed.

| Variable | Without it |
|---|---|
| `TMDB_API_KEY` | No metadata or artwork from TMDB. |
| `TVDB_API_KEY`, `TVDB_PIN` | No season and episode structure from TheTVDB, so a series added from that provider arrives with no episode tree. |
| `OPENSUBTITLES_API_KEY` | Subtitle searches return nothing. Everything else is unaffected. |

Indexers are not configured here; you add them in the application, after the first-run setup.

### Telemetry: all optional, and off by default

| Variable | Default | Meaning |
|---|---|---|
| `CINOMNI_OTLP_ENDPOINT` | *(empty)* | An OpenTelemetry collector to export traces, metrics and logs to. Empty means no exporter is registered and **nothing leaves the process**. It must be an absolute `http(s)` URL and the collector must be reachable from the application container; a value that cannot be honoured stops the start instead of exporting to nowhere. |
| `CINOMNI_OTLP_PROTOCOL` | `grpc` | `grpc` (port 4317) or `httpprotobuf` (port 4318). Anything else stops the start. |
| `CINOMNI_JSON_LOGS` | `false` | Emit log lines as JSON. Independent of the exporter, for a file-based log shipper. |

Section 10 says what is exported, and what is deliberately never in it.

---

## 4. The storage contract

This is the one section that is not optional reading. Two rules, both enforced by how the modules
already work rather than by anything you can configure around.

**One filesystem.** A completed download is *hardlinked* into the library, not copied: the file lands
once and the torrent keeps seeding from the same bytes. A hardlink cannot cross a filesystem, so if
`downloads/` and `library/` are separate mounts, every import becomes a full byte-for-byte copy:
twice the disk, for the life of the installation. That is why `docker-compose.yml` mounts **one** host
directory at `/data` and puts both trees inside it.

It is not silent. At every start Cinomni links a zero-byte probe file from the staging root into the
library root, and warns (naming both configuration keys and the error the kernel returned) when that
fails; a warning and not a crash, because copies still import and an operator who accepts them must
not be locked out. It says so again, once per import job, if a hardlink actually falls back, and each
file's row in `import.file_operations` records `Copy` rather than `Hardlink`, so the installation's own
record of what happened to your file is the outcome and never the intention. Section 10 has both lines.

A job resumed after a restart can meet a file that landed during the attempt that was interrupted. It
asks that file rather than assuming: the destination and the download are one file, or they are two, and
the row says `Hardlink` or `Copy` accordingly. Nothing is written to find out, because the download is still
seeding from that directory. A `type` of `Unknown` in one of those rows means the filesystem would not
supply the identity that answers it (a path that went away between the two reads is enough, so this is
reachable on any platform), and it is recorded instead of a guess. It is not silent either: the job logs
one line saying so, because "the record does not know" is something you have to be able to find out
before you remove a download. Section 10 has that line too.

**Links stay inside their root.** A symbolic link inside the library or the staging area must point
somewhere inside that same tree. Cinomni resolves links before it decides a path is inside its root, and
refuses one that leads out: a torrent can create links, and one aimed at your library or the host
would otherwise be imported as content. So `library/Movies -> /mnt/archive/Movies`, or a recycle folder
linked to another disk, now refuses every landing into it, with the reason in the job's history. To put
the library on another disk, point the **root** at it instead: a root reached through a link of its own
keeps working. The scan of a download does not follow links at all.

**The same absolute path in both containers.** When a download starts, the application hands the
sidecar its own configured staging path as the download's save location, and later reopens the landed
file at that same path. If the two containers see the directory at different paths, downloads complete
and every import then fails to find its content. `docker-compose.yml` mounts `/data` in both, which
satisfies this; keep it that way if you change the layout.

**The same account in both containers.** For the same reason, both run as `CINOMNI_UID:CINOMNI_GID`.
A modern kernel refuses a hardlink to a file owned by someone else (`fs.protected_hardlinks`), so a
uid mismatch degrades imports into copies exactly as a second filesystem would.

**The sidecar sees only `downloads/`.** The application gets the whole `/data`; the sidecar gets
`/data/downloads` and nothing above it. It is the process that parses data straight from the swarm, so
it is not given the library it never needs to touch, nor the recycle bin that holds your last copy of
something. Keep it that way if you change the layout: the path must stay identical on both sides, but
the sidecar's mount must stay a subtree.

What lives where:

| Path | Contents | Backup? |
|---|---|---|
| `/data/library` | Your media, organised and renamed. | Yes, **and it is your job**, not Cinomni's. Section 6. |
| `/data/library/.recycle` | Where a superseded file is set aside when a better release replaces it. **Nothing ever empties it**: deleting a household's only copy of something is not a decision Cinomni automates. Review it yourself. Nothing in it is ever overwritten: a second copy of the same name set aside at the same moment gets a numbered name. | It is your safety net. |
| `/data/downloads` | Torrent staging. Files stay here while seeding. | No. |
| `/data/transcodes` | Just-in-time HLS output, on its own volume to keep the churn off the media disk. A stream's directory goes when the stream ends; after a restart, a conversion that had finished is picked up again and anything half-done is removed. | No, it is scratch. |
| `/data/backups` | Database dumps Cinomni takes itself, on a volume of its own. **A credential store** (section 6). | It *is* the backup. Copy it off this machine. |
| `cinomni-pgdata` volume | The database: catalog, monitoring, decisions, progress, accounts. | **Yes**, and Cinomni does this one for you. Section 6. |

---

## 5. First run

```bash
docker compose pull        # the images of the version CINOMNI_IMAGE_TAG names
docker compose up -d
docker compose ps          # wait for cinomni to report "healthy"
```

Nothing is built on this machine: every image is published, at each version, on GitHub Container
Registry (`ghcr.io/cinomni/...`). To build from a checkout of the source instead, add
`docker-compose.build.yml` (`-f docker-compose.yml -f docker-compose.build.yml up -d --build`).

The first start is the slow one: the application applies every module's migrations before it serves
anything, and only then recovers interrupted work. The container's health probe allows two minutes for
this; if your hardware needs more, raise `--start-period` in the `HEALTHCHECK` line of the Dockerfile.

Then open `http://127.0.0.1:8080/`. The first screen is the administrator setup: the first account
you create is the operator.

After that you add your indexers and your first title. Cinomni ships no indexer and no list of sites.
You either add an indexer by hand (a Torznab/Newznab endpoint, or a definition you upload), or
subscribe to an **indexer catalog source**: an https URL you choose that publishes a JSON manifest of
indexer definitions, which you can then install one by one. In the console, open *Indexers*, then the
catalog, and add the source's name and URL. Cinomni fetches it once when you add it and again whenever
you press *Refresh*; the last valid manifest is kept in the database, so the catalog survives a restart
and the source being down. Disabling or removing a source only changes what the catalog lists: the
indexers you installed from it stay and keep working. The manifest format, and every rule a manifest
must pass, is in [docs/indexer-catalog-format.md](./docs/indexer-catalog-format.md).

Before you add any: with
`CINOMNI_TORRENT_EGRESS=direct` every download you start from here is made over this machine's own
internet connection, and your public address is visible to everyone else in the swarm. Section 1 has
the whole picture, and section 8 has the tunnel option.

---

## 6. Backup and restore

Cinomni backs up **its database**. It does not back up **your media**. Both halves matter, and only
one of them is automatic.

### What you would lose, and whether you could get it back

Irreplaceable (nothing can recreate it, and it is what the dump exists for):

| | |
|---|---|
| Accounts and permissions | Password verifiers cannot be reversed or reissued from anything. Losing them locks the household out. |
| The library index | Which file is which title, episode and version, and where it sits on disk. |
| Playback progress | Where everyone was in everything. |
| Requests | What the household asked for and what was decided. |
| Monitoring policy and acquisition intents | What is being watched for, and what is in flight. |
| **Decision reasons** | Why a release was chosen over the others, and why a file was replaced. This is the answer to "why is *this* copy on my disk", and it exists nowhere else. |
| Download checkpoints | Resume data lives in the database, not on disk, so a restored installation continues downloads instead of starting them again. |
| The outbox and command queue | Work that was in flight when the dump was taken; it resumes on the next start. |
| **Your media files** | Under `/data/library`, including `.recycle`. Cinomni never copies them. See below. |

Reproducible (losing it costs time, not information):

| | |
|---|---|
| Metadata snapshots and artwork | Re-fetched from the providers. Artwork is referenced by URL, never stored here. |
| Search executions and results | Re-run against your indexers. |
| Parsed release names | Recomputed from the release. |
| `/data/downloads` | Torrent staging. Re-downloadable. |
| `/data/transcodes` | HLS cache. Pure scratch. |
| Provider API keys | They are in your `.env`, never in the database. Back that file up separately, and treat it as the secret it is. |

**The media is your job.** Cinomni will not copy tens of terabytes anywhere, and pretending otherwise
would be the most dangerous sentence in this document. Back up `CINOMNI_DATA_DIR/library`, including
`.recycle` (which holds the last copy of anything a better release replaced), with whatever you already
use for your own files. Subtitle files sit beside their video and are covered by the same copy.

### What is taken, and when

Once a day, Cinomni dumps every one of its sixteen schemas over a single connection, so the whole
database is one consistent snapshot rather than sixteen unrelated ones. Each backup is two files in
`/data/backups`:

- `cinomni-<timestamp>.dump`: the archive, in PostgreSQL's custom format;
- `cinomni-<timestamp>.manifest.json`: what it contains, its SHA-256, and the migrations each schema
  had applied. The manifest carries no host, user or password, so it is safe to paste into an issue.

The newest seven are kept and older ones removed, after a successful run only, so a failed backup never
costs you a good one. A run interrupted half-way leaves a `.partial` file that is not, and never
becomes, a backup; a run sweeps the ones older than a day before it writes its own, so an installation
that keeps failing does not fill its disk with the evidence.

The dump runs on a worker of its own, not on the shared command queue: that queue has a single worker
that runs one command at a time, and an hours-long dump on it would hold up everything else the
installation had queued, the download checkpoints included.

**Every attempt is recorded**, whether or not it produced anything, in `operations.backup_run`. That
is how you answer "when did this actually last back itself up", and it is the only place the answer
lives, because there is no HTTP surface for backups:

```bash
docker compose exec postgres psql -U cinomni -d cinomni -c \
  "SELECT started_at, triggered_by, outcome, stamp, dump_size_bytes, reason
     FROM operations.backup_run ORDER BY started_at DESC LIMIT 10;"
```

`Succeeded` wrote a backup. `Failed` says why in `reason`, and is retried within the quarter hour
rather than waiting for tomorrow. `Skipped` means another run already held the lock (an operator's
`backup create` while the schedule fired, say), so a backup was being taken, just not by that run.
`Interrupted` is a run that was killed part-way; the next run finds it and closes it.

| Variable | Default | Meaning |
|---|---|---|
| `CINOMNI_BACKUP_DIR` | the `cinomni-backups` volume | Where dumps land. Point it at a host path to copy them off the machine, owned by `CINOMNI_UID:CINOMNI_GID`. Keep it outside `CINOMNI_DATA_DIR`; nothing verifies that for you, see below. |
| `CINOMNI_BACKUP_KEEP` | `7` | How many are kept. |
| `CINOMNI_BACKUP_INTERVAL` | `1.00:00:00` | How often one is taken (`d.hh:mm:ss`). |
| `CINOMNI_BACKUP_ENABLED` | `true` | `false` stops the schedule. Taking one by hand still works. |

By hand, which you should do before every upgrade:

```bash
docker compose exec cinomni dotnet Cinomni.Host.dll backup create
docker compose exec cinomni dotnet Cinomni.Host.dll backup list
docker compose exec cinomni dotnet Cinomni.Host.dll backup verify <timestamp>   # re-hash the archive
docker compose exec cinomni dotnet Cinomni.Host.dll backup check  <timestamp>   # may this be restored here?
```

> ### A dump is a credential store
>
> It is **not encrypted**, and it contains delivery-channel URLs (a webhook URL *is* the credential),
> indexer addresses (which commonly carry an API key in the query string), download links, Argon2id
> password verifiers and session token hashes.
>
> The files are written readable by the application's account only, and Cinomni refuses to start if
> `Backup:Root` overlaps the library, downloads or transcode roots in either direction.
>
> **That check compares the paths inside the container, and it cannot see where you mounted them
> from.** In the packaged deployment `Backup:Root` is always `/data/backups`, so setting
> `CINOMNI_BACKUP_DIR` to something inside `CINOMNI_DATA_DIR` starts cleanly and is never reported.
> Do not do it. `CINOMNI_DATA_DIR/library/backups` puts an unencrypted credential store in the tree
> you share over SMB or rsync to a NAS; `CINOMNI_DATA_DIR/downloads/backups` is worse, because that
> directory is also mounted into the torrent sidecar, the least trusted process in the product, with
> no authentication on its control port. Keeping the host path out of the media tree is yours to get
> right, and wherever you copy a dump, treat it exactly as you treat `.env`.
>
> There is deliberately **no way to reach a backup over the network**: no API route, no page, no
> download link. One authorization mistake would hand over the whole installation. You reach a dump
> through the filesystem of the machine you already have shell access to.

### Getting a backup off this machine

A backup that only exists on the disk that failed is not a backup.

```bash
# Named volume (the default):
docker run --rm -v cinomni_cinomni-backups:/backups -v "$PWD:/out" alpine \
  sh -c 'cp /backups/cinomni-*.dump /backups/cinomni-*.manifest.json /out/'

# Or set CINOMNI_BACKUP_DIR to a host path and rsync it like anything else.
```

### Restoring

Restoring is not a button, and that is deliberate: it replaces the entire database, so the application
must be stopped, and a "replace my installation" endpoint is not a risk worth carrying.

**It runs inside the application container, not on the host.** The database publishes no port and sits
on an internal network (`docker-compose.yml`), so nothing on the host can reach it, and the host has
no `pg_restore` either. The application image is the one place that has both: the PostgreSQL 16 client,
`sha256sum`, `curl`, a mount of `/data/backups`, and a route to the database. The script ships in it at
`/app/scripts/cinomni-restore.sh`.

```bash
# 1. Will this dump work with the build you are about to run? This is the gate, and it runs FIRST,
#    while the container is still up. (Already stopped? docker compose run --rm cinomni backup check <timestamp>)
docker compose exec cinomni dotnet Cinomni.Host.dll backup check <timestamp>

# 2. Stop the application. The database stays up.
docker compose stop cinomni

# 3. Restore, into the database recreated for it. Type the password at the prompt rather than putting
#    it in the command: an argument vector is world-readable, and a shell history file outlives you.
#    `-e PGPASSWORD` (no value) forwards it from this shell, so it never appears in the command.
read -rs PGPASSWORD && export PGPASSWORD
docker compose run --rm --no-deps \
  -e PGPASSWORD \
  -e CINOMNI_HEALTH_URL=http://cinomni:8080/health \
  --entrypoint /bin/sh cinomni \
  -c 'sh /app/scripts/cinomni-restore.sh \
        --manifest /data/backups/cinomni-<timestamp>.manifest.json \
        --host postgres --port 5432 --username cinomni \
        --database cinomni --recreate \
        --checked --yes'
unset PGPASSWORD

# 4. Start it again and confirm the probe.
docker compose start cinomni
curl -sf http://127.0.0.1:8080/health
```

`--host postgres` is the database's name on the internal network, and `CINOMNI_HEALTH_URL` points the
"is it still running" probe at the *service* rather than at this throwaway container's own loopback,
where nothing ever listens. On a host that genuinely can reach the server (a database you run
yourself, outside this compose file), the same script works there unchanged with `--host`/`--port` set
to it.

> **This procedure has been run**, on 2026-07-30, against a real daemon: a throwaway stack with the
> same shape as the one above (an internal network, a database with no published port), using the
> image this `Dockerfile` builds. All four steps as written, including the `docker compose run` above
> verbatim. It restored the sixteen schemas, and the Host came back with nothing to migrate and
> `/health` answering 200. Three refusals were exercised in the same stack: the same command while the
> application was still up, a target that still held schemas, and a maintenance database the role
> could not reach.
>
> One thing only a real run shows. With the torrent sidecar down, `/health` took longer to answer than
> the probe's three-second budget, so the HTTP question came back inconclusive and it was the session
> count that refused the restore. That is the fallback below doing exactly what it is for, and it is
> why a probe that reaches nothing is never read as "nothing is running".

The script re-hashes the archive against its manifest, prints what it is about to replace, and does
nothing without both `--checked` and `--yes`. Its "the application must be stopped" guard fails closed
and asks two questions:

- **anything answering the health URL refuses**: any HTTP status, because a Host reporting `503` while
  a dependency is down still has its relay, command worker and scheduler driving the database;
- **a probe that reaches nothing is not proof**, so it then asks the server whether any client session
  is attached to the database it is about to replace. A running Cinomni always holds one. If neither
  question can be answered, it refuses.

`--no-health-check` skips both, and is the way to say you have confirmed it yourself.

#### It needs a maintenance database, on every run

The session question is asked over a connection to a *different* database from the one being restored,
and it has to be: the target may be dropped a moment later, and when you restore into a new database it
does not exist yet. That connection is made on **every** run, including one without `--recreate`, which
never touched another database before this guard existed.

It defaults to `postgres`, so the restoring role needs `CONNECT` there. A role without it is refused
(fail-closed is the point), and the refusal names the way out rather than leaving you to find it:

- pass **`--maintenance-db <name>`** with any database that role *can* connect to. `pg_stat_activity`
  is cluster-wide, so every database in the cluster answers the same question; the flag only chooses
  where to stand while asking. `template1` works on a default cluster, and so does the Cinomni database
  itself when you are not recreating it;
- it must **not** be the database you are restoring when you pass `--recreate`. Nothing can drop a
  database from a session connected to it, so the script refuses that combination up front instead of
  half-way through;
- there is **no flag that skips only this question**. `--no-health-check` skips the whole guard, and
  passing it is you stating you have confirmed the application is stopped yourself.

> **The hash detects a bad copy, not a substituted one.** The expected SHA-256 lives in the manifest
> beside the archive, and neither file is signed, so anyone able to write both passes `backup verify`,
> `backup check` and this script's own comparison. They are integrity and compatibility gates, not
> authenticity ones. A dump from a location you do not control must not be restored: `pg_restore`
> executes the archive's SQL as the database owner.

Two rules it enforces that are easy to get wrong on your own:

- **The target must be empty.** `pg_restore --clean` looks like the convenient answer and does not
  survive this schema: it cannot drop a partitioned table's inherited primary key, and dies part-way
  through with the database in neither state. `--recreate` drops and recreates it; without that flag
  the script refuses a database that still holds schemas, and refuses just as firmly when it could not
  ask. A count query that failed says nothing about a database, and a database nobody could inspect is
  not an empty one; it is reported as "could not be inspected", which usually means the target does not
  exist yet or this role cannot connect to it. Restoring into a *new* database and repointing
  `ConnectionStrings__Cinomni` at it is the safest variant: the old one stays untouched.
- **`backup check` refuses a dump from a newer build.** Restoring one would leave your code running
  against tables it has no model for, with no migration back. An *older* dump is fine and expected:
  the application migrates it forward on the next start and says which migrations it will apply.

Afterwards:

- sign every account out if the dump is old. It restored the session tokens that were valid when it
  was taken, including any you revoked since;
- anything acquired after the dump is unknown to the installation again. The files are still on disk;
  re-importing them is the way back.

**The PostgreSQL client's major version must match the server's.** The image ships
`postgresql-client-16` because the database is PostgreSQL 16. A newer client dumps an older server
fine, but restoring an archive with a newer `pg_restore` fails on a setting the older server has never
heard of. When the database moves to 17, the client in the `Dockerfile` moves with it. A backup taken
with a mismatched client logs a warning saying exactly that.

### The drill

A restore procedure nobody has run is a hope, not a plan.
`tests/Cinomni.Host.Tests/BackupRestoreDrillTests.cs` is that procedure as an executable test: it
seeds an account, a download checkpoint and a queued command into a real database, takes a real backup
with the real `pg_dump`, **destroys the database**, restores it, and then asserts the thing that
actually matters: that this build has zero pending migrations against the restored database and its
whole startup sequence runs cleanly. It runs on every CI push, against the client that job installs
rather than whatever the bare name happens to resolve to. On your own machine it runs too, if
`postgresql-client-16` is installed; without it, it reports why to the test output and stops, and
because xUnit 2.9.3 cannot mark a test skipped from inside it, that run is still *reported* as passed.
CI is the gate that keeps the drill honest, not your local run.

Run your own drill once a quarter with a real dump, restored into a scratch database. Nothing is
stopped for this one, because it never touches the database the installation is using:

```bash
# A scratch database, created inside the postgres container: it has no published port either.
docker compose exec postgres createdb -U cinomni cinomni_drill

read -rs PGPASSWORD && export PGPASSWORD
docker compose run --rm --no-deps -e PGPASSWORD --entrypoint /bin/sh cinomni \
  -c 'sh /app/scripts/cinomni-restore.sh \
        --manifest /data/backups/cinomni-<timestamp>.manifest.json \
        --host postgres --port 5432 --username cinomni \
        --database cinomni_drill \
        --checked --no-health-check --yes'
unset PGPASSWORD
# then point a throwaway Cinomni at cinomni_drill and confirm /health
```

`--no-health-check` is correct here and only here: the drill deliberately restores into a database
nothing is connected to, while the running installation keeps its own. Do not carry that flag over to a
real restore. That flag is also why this one asks nothing of the maintenance database: it skips the
guard that needs it. The `createdb` above is not optional either: the script refuses a target it cannot
inspect, and a database that does not exist yet is one of those.

---

## 7. Upgrading

Take a backup first (`backup create`, section 6). Then point `CINOMNI_IMAGE_TAG` in `.env` at the new
version, or use the `docker-compose.yml` attached to its GitHub release, which already names it, and:

```bash
docker compose pull
docker compose up -d
```

Read the version's release notes before you do: an alpha, beta or rc can need a step of its own, and
the notes say so (*Versioning* in CHANGELOG.md). Migrations run automatically, before the application serves any traffic. **Rolling back to an older
image after an upgrade is not supported**: the database has already moved forward and the previous
build does not know its shape. Take a backup before every upgrade (`backup create`, section 6) and
restore that if you need to go back.

### If you are locked out of an account

Two-factor authentication is a way to lose an account as well as a way to protect one, and on a
single-administrator installation losing it means losing the server. Recovery codes are the first
answer: ten are minted when the factor is confirmed, shown once, and each works once. **They also
work when `CINOMNI_SECRET_KEY` is gone or has changed**: the TOTP secret is encrypted with that key
and becomes unverifiable, while a recovery code is only hashed.

When the authenticator and all ten codes are gone, the way back is the database. It needs a shell on
the machine and the database credentials, which is already total control, so it grants nothing that
access did not already have:

```bash
docker compose exec -T postgres psql -U cinomni -d cinomni <<'SQL'
-- Usernames are stored lower-case.
UPDATE identity.users
   SET totp_secret_cipher = NULL,
       totp_secret_nonce  = NULL,
       totp_secret_key_id = NULL,
       totp_confirmed_at  = NULL
 WHERE username = 'admin';

DELETE FROM identity.recovery_codes
 WHERE user_id = (SELECT id FROM identity.users WHERE username = 'admin');
SQL
```

That account then signs in with its password alone and can enroll again. Nothing else about it
changes, and no other account is touched. Take a backup first (section 6): this is a hand-written
`UPDATE` against a live installation, and the `WHERE` clause is the only thing keeping it to one row.

### Which build am I running?

Signed in, any account: `GET /api/system/info` answers with the version, the full informational
version, the commit and the build date. The web client shows the same thing in its footer and under
**Console → System**. It is deliberately not on `/health`, which is anonymous.

A published image is stamped by the release workflow, so it reports its version, commit and build
date. An image you build yourself (`docker-compose.build.yml`) is **not stamped**: it reports the version the
source declares (`<Version>` in `src/Directory.Build.props`) and `null` for the commit and the build
date. That is honest rather than broken (the
build genuinely does not know which revision it came from), and it is what every development build
looks like. Nothing depends on the stamp; it exists so that two images can be told apart.

Stamping is three build arguments. `.github/workflows/release.yml` passes all of them for every
published image; to stamp one by hand:

```bash
docker build \
  --build-arg CINOMNI_VERSION=0.1.0-alpha.1 \
  --build-arg CINOMNI_COMMIT="$(git rev-parse HEAD)" \
  --build-arg CINOMNI_BUILD_DATE="$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --build-arg CINOMNI_SOURCE_URL="https://github.com/Cinomni/cinomni/tree/$(git rev-parse HEAD)" \
  -t cinomni:0.1.0-alpha.1 .
```

`CINOMNI_VERSION` is the version exactly as *Versioning* in [CHANGELOG.md](./CHANGELOG.md) spells it
(three numbers, optionally an `alpha`, `beta` or `rc` stage, never a `v` prefix), and should match the
source's own `<Version>`. `CINOMNI_COMMIT` becomes the build metadata of the informational version, so
`0.1.0-alpha.1` published from commit `a1b2c3d…` reports an informational version of
`0.1.0-alpha.1+a1b2c3d…`, and a plain version of `0.1.0`. Anything in that field that
is not a plausible revision is reported as no commit at all, rather than shown to you as one.
`CINOMNI_SOURCE_URL` is embedded in the web client and is shown on sign-in, playback and normal
pages. The example URL is valid only when the image was built from that exact public Cinomni
revision. A fork or modified image must use a URL to its **own corresponding source**, including
the build scripts, and keep that URL available to its network users. A link to upstream `main`
does not satisfy this for a modified build.

**No workflow builds the application image today**: `.github/workflows/image.yml` builds and tests
the torrent sidecar only. Until one exists, a stamped image is one somebody built with the command
above.

---

## 8. The VPN overlay (opt-in)

`docker-compose.vpn.yml` puts the torrent sidecar inside a tunnel container's network namespace and
makes Cinomni stop downloading when that arrangement stops verifiably holding:

```bash
docker compose -f docker-compose.yml -f docker-compose.vpn.yml up -d
```

Set `CINOMNI_TORRENT_EGRESS=vpn` in `.env` when you use it, and fill in the VPN block of `.env`.
`VPN_SERVICE_PROVIDER` has no default and aborts the start, as `CINOMNI_SIDECAR_CONTROL_TOKEN` does in
every topology.

It is opt-in in the full sense. With the overlay off, Cinomni needs no tunnel, asks for none, and
behaves exactly as it does today, local development included.

### What it actually does

Four layers, and each one is useless without the others:

1. **The namespace.** The sidecar keeps no addresses and no networks of its own and publishes no
   ports. The only interfaces it has are the tunnel container's.
2. **The tunnel container's packet filter**, which drops everything that is not the tunnel or an
   explicitly listed exception and which stays loaded when the tunnel goes away. A kill-switch that
   dies with the tunnel is not a kill-switch. Cinomni configures exactly one exception, port 50051,
   so the application can reach the sidecar, and configures no VPN-side input port at all. That the
   exception applies only to the local side is the tunnel image's documented behaviour rather than
   something Cinomni observed; step 3 of the drill below is where you confirm it on your own stack.
   What Cinomni does enforce itself is the credential on that port, which this topology requires.
3. **libtorrent bound to the tunnel device by name**: both `listen_interfaces` and
   `outgoing_interfaces`, because either alone leaves a direction unbound. Binding by device rather
   than by address matters: an address binding breaks the moment the provider hands out a new one,
   and silently stops binding at all. DHT, local peer discovery and port mapping stay off in every
   mode; each of them talks to something the tunnel was chosen to hide from.
4. **Cinomni observing where its traffic would actually go**, every `CINOMNI_TUNNEL_POLL_INTERVAL`,
   and holding every download when the answer changes. This is the layer the other three cannot
   provide: a filter that is loaded and a filter that works are not the same claim.

Three independent facts have to hold for the traffic to count as verified, and each fails on its own:
the tunnel device exists and carries an address; the default route leaves through that device; and
the source address the kernel would put on the next outbound packet is the tunnel's own. The third is
the leak check, and it is the one the first two miss: a policy route or a second table can carry
traffic off the box while the tunnel sits there looking healthy. It is taken silently: a `connect()`ed
UDP socket performs the full route lookup without sending anything, so the probe cannot itself become
the leak it is looking for.

The third fact is asked of **both address families**. A tunnel that carries IPv4 only leaves native
IPv6 egress intact, and libtorrent will announce and connect over it with your own address. That is the
most common real-world leak, and the one an IPv4-only check reports as verified. If an IPv6 packet
would leave with an address the tunnel device does not hold, the egress is not verified
(`ipv6-egress-not-the-tunnel`). A container with no IPv6 egress at all (the ordinary case) cannot
leak that way and is not penalised for it.

Cinomni also refuses to trust an answer that only *looks* healthy. An observation older than three
poll intervals is a recording rather than a check (`tunnel-observation-stale`), and a sidecar that
reports enforcing a weaker policy than `CINOMNI_TUNNEL_LOSS_POLICY`, or watching a different device,
is a half of the kill-switch that will not act when the other half believes it has
(`tunnel-policy-divergent`, `tunnel-device-divergent`). All three are treated as unverified.

### What it does not do

- **It does not put the rest of Cinomni behind the tunnel.** The application keeps its own egress for
  indexers, metadata and subtitles. Only the swarm is sealed. A tunnel carrying the whole installation
  would also carry the interface you sign in to.
- **It does not vet the image you choose.** The default is pinned; a substitute is your judgement.
- **It does not encrypt the control channel.** See "The control port" below.

### When the tunnel drops

Downloads stop, keep their progress, and resume by themselves when the tunnel comes back. Nothing is
lost, nothing is re-searched, and no acquisition is returned to searching: a hold is not a failure.
Each held download records why on its own history, and the whole installation's state is on
`GET /api/downloads/tunnel` (administrator only) and in an administrator notification.

"Held" is Cinomni's own decision and holds whether or not the sidecar can be reached: no new release
is handed to the engine, no manual resume is accepted under `block`, and the state survives a
restart. On top of that Cinomni asks the engine to pause each transfer it held, and asks it to resume
them on the way out. This is best effort, because the sidecar is exactly what may have failed. A pause it
could not deliver is a warning in the log, never a hold that silently did not happen.

Turning the guard off releases what it was holding. Remove `CINOMNI_TUNNEL_DEVICE` (the documented
way back to a direct connection) or set the policy to `ignore`, and the next cycle lifts every hold
and lets the downloads run again over whatever route exists. Nothing has to be repaired by hand.

`CINOMNI_TUNNEL_LOSS_POLICY` decides how hard that is enforced:

| Policy | Holds downloads when | Manual resume | Sidecar without its tunnel |
|---|---|---|---|
| `block` (default) | the **first** observation that is not a verification, including one that could not be taken at all, since an unreachable sidecar is not evidence that its traffic is safe | refused | refuses to start |
| `pause-and-alert` | two consecutive failed observations, so a single blip is absorbed | allowed, and recorded on the task's history | starts, with a warning |
| `ignore` | never. Observed and reported only | not applicable | starts, with a warning |

Anything below `block` is logged as a warning at every start, naming the mode, and is visible on the
health surface. A value that is not one of the three becomes `block`: a typo cannot weaken an
installation.

The two columns are about different things, and the difference is deliberate. Under `block` and
`pause-and-alert` alike, the sidecar stops its own traffic on the **first** failed observation. That
is the immediate stop, taken in the process that owns the sockets, before the application has even
noticed, and it covers seeding torrents as much as downloads. What the policy changes is when the
**durable** decision is taken: how quickly a household's downloads are actually paused, notified about
and refused a manual resume. `pause-and-alert` buys patience with the paperwork, never with the
packets. `ignore` is the one mode that lets traffic continue over whatever route exists.

A manual resume under `pause-and-alert` lifts both stops: the download's own hold and, for the rest of
that outage, the sidecar's. Every download the application still holds stays paused at the engine, but
a finished one that is seeding starts uploading again: resuming anything while egress is unverified is
a decision for the whole session, not one torrent. The sidecar stops the traffic again at the next
outage.

If the tunnel container restarts, it comes back in a new network namespace and the sidecar would be
left in the old one, with no device and no route. Compose restarts the sidecar along with a tunnel it
recreates; for a restart Compose does not drive, the sidecar exits once its tunnel device has been
missing for two minutes (`SIDECAR_TUNNEL_ORPHAN_SECONDS`), and its restart policy starts it again in
the tunnel's current namespace. The application re-establishes its downloads once egress verifies.
Seeding does not come back: a finished download the new sidecar does not hold is closed as
`SeedingLost` (see *Downloads* in section 3), so a tunnel outage longer than two minutes ends seeding.

### The control port

The sidecar's gRPC port adds torrents, decides where they are written and removes them with their
files. Outside the tunnel it lives on a private Compose network with nothing published, but anything
else that joins that network could reach it. Inside a tunnel's namespace it is bound inside a network
you do not own, and "whoever can reach the address" becomes "whatever else the tunnel can route to".
So every topology **requires** a credential (`CINOMNI_SIDECAR_CONTROL_TOKEN`, compared in constant
time), and the sidecar refuses to start without one. The application reaches it directly, never
through `HTTP_PROXY` or `ALL_PROXY`, so the token cannot be carried anywhere else.

It is a shared token and not TLS, deliberately. A certificate proves *who* the peer is, and there is
no second identity here to distinguish: one client exists, it is deployed in the same unit as the
server, and the channel never leaves the namespace. What is missing is proof that the caller is *the*
client, which a shared secret gives directly and which you rotate by restarting two containers. TLS
would add a certificate lifecycle (issuance, renewal, an expiry that silently stops every download
one morning) to buy confidentiality against an observer that cannot exist on this path. If that
channel ever crosses a host boundary, the trade reverses and the decision has to be re-made.

### Using a different tunnel image

The default is gluetun, unmodified and pinned by digest (section 12). Set `CINOMNI_VPN_IMAGE` to use
another, if it satisfies the same contract:

- it creates the interface named by `CINOMNI_TUNNEL_DEVICE` and holds the default route through it;
- it filters everything else, and stays filtered when the tunnel is down;
- it declares a `HEALTHCHECK`. Compose refuses the dependency outright if it does not, which is the
  right way to find out;
- it runs with `NET_ADMIN` and `/dev/net/tun` and nothing else. Every other capability is dropped; if
  your image needs more, it fails loudly and you add what it needs knowingly.

Cinomni verifies the outcome, not the brand: an image that satisfies the contract passes the same
checks the default does, and one that does not is held closed by the same policy.

### The drop drill

Everything above is tested automatically except the one thing that matters most: a real tunnel
actually dropping. Nothing in CI can create one. Run this against your own installation, on a copy of
your data, before you trust it, and again after any change to the overlay or the tunnel image.

> **This drill has never been run.** It is written from the design and from the automated coverage,
> not from an observed outcome. The log at the end of this section is empty on purpose; the first
> person to run it fills in the first row.

1. **Arrange.** Start with the overlay, `CINOMNI_TUNNEL_LOSS_POLICY=block`, and a download in flight
   with visible peers. Record what the swarm sees:
   ```bash
   docker compose exec vpn wget -qO- https://ipinfo.io/ip     # the tunnel's address, not yours
   docker compose exec vpn ip route show default              # default via the tunnel device
   ```
   Expect the first to differ from your household's public address. If it does not, stop: nothing
   below is meaningful and the tunnel is not carrying traffic in the first place.
2. **Drop it three different ways**, one at a time, restoring in between. They fail differently and a
   kill-switch that survives one can still miss another:
   ```bash
   docker compose exec vpn ip link set "${CINOMNI_TUNNEL_DEVICE:-tun0}" down   # the interface goes
   docker compose exec vpn ip route del default                                # the route goes
   docker compose stop vpn                                                     # the whole container goes
   ```
3. **Assert, for each of the three:**
   - torrent traffic stops. Watch the transfer rate in the interface, and confirm from outside that
     no BitTorrent connection is leaving over your own address;
   - the tunnel container's filter is still loaded, and the one exception Cinomni asks for is on the
     local side only. Whatever your image's equivalent of the two commands below is, the policy must
     still be "drop" after the tunnel is gone (the classic regression is a tunnel process that
     flushes its own rules on the way out), and port 50051 must not be reachable from the tunnel
     side. Paste the ruleset into the log at the end of this section; it is the one claim in this
     file that rests on the tunnel image's documentation rather than on Cinomni's own code:
     ```bash
     docker compose exec vpn iptables -S INPUT
     docker compose exec vpn ip6tables -S INPUT
     ```
   - `GET /api/downloads/tunnel` reports `verified: false` with a `reason`, and `heldTaskCount`
     matching the number of transfers you had;
   - every affected download shows **Paused** with a `NetworkHold` line on its history, carrying the
     same reason;
   - an administrator notification arrives, and the downloads view says the transfers are on hold;
   - **no download failed**, and no acquisition went back to searching. This is the assertion people
     forget, and getting it wrong costs indexer quota and progress for the length of the outage;
   - a manual resume is refused with `409 downloads.network_held`.
4. **Restore** the tunnel each time and confirm the downloads resume by themselves, from where they
   stopped (no full recheck, no re-download), and that a "tunnel is carrying traffic again"
   notification arrives.
5. **The negative control.** A drill that only ever passes proves nothing. Deliberately weaken the
   installation once: set `CINOMNI_TUNNEL_LOSS_POLICY=ignore`, restart, and drop the tunnel again.
   The health surface must still report `verified: false`, the startup log must carry the warning
   naming `Ignore`, and the downloads must **not** stop. If they stop anyway, the policy is not
   really configurable; if the surface reports `verified: true`, the detector is broken and every
   `block` result above was luck. Downloads held by the previous step must also be **released** by
   this one, within a poll: weakening the policy has to be able to let go of what the strong one
   took, or the drill itself would strand the installation. Put the policy back afterwards.
6. **The way out.** Once, drop the tunnel under `block`, confirm the downloads are held, then restart
   without the overlay (`docker compose -f docker-compose.yml up -d`, which leaves
   `Downloads__Tunnel__Device` unset), which is the documented route back to a direct connection. Within one
   poll every held download must return to transferring on its own, and `GET /api/downloads/tunnel`
   must report `configured: false`. Nothing should need repairing by hand: a hold that only the
   tunnel's return can lift is a hold an operator cannot escape.

| Date | Provider and protocol | Tunnel image digest | Outcome |
|---|---|---|---|
| _(never run)_ | | | |

---

## 9. Transcoding and hardware acceleration

The base image transcodes in software only. Two opt-in overlays add a hardware encoder backend on
Linux, and neither is applied by default:

```bash
# Intel/AMD, via VAAPI and QSV — /dev/dri, no external toolkit (uses the -hwaccel image)
docker compose -f docker-compose.yml -f docker-compose.hwaccel.yml up -d

# NVIDIA, via NVENC — needs nvidia-container-toolkit already installed on the host
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d
```

Applying an overlay makes a hardware backend *possible*; it does not make it used. What actually
happens each start and each session is decided in three independent places, in order:

1. **The hardware test** (`StartupChecks.ReportHardwareCapabilitiesAsync` at every start, and again
   whenever an administrator asks for it). It does not take FFmpeg's word for anything: every backend
   that could exist on this platform is made to encode, and then to decode, a few generated frames,
   and only what worked is reported ("The hardware test" below has the detail). The start logs either
   `Hardware transcoding: detected Vaapi, Qsv at startup.` or `Hardware transcoding: no
   VAAPI/NVENC/QSV/AMF backend passed its test at startup. Transcoding uses software x264.` Nothing
   here can fail the start: a missing binary, a hung driver and a failed test all degrade to software.
2. **The operator's settings**, Console → Settings → Playback. *Hardware transcoding* (on by default)
   turned off forces every transcode to software regardless of what the test found, without removing
   the overlay or restarting; *Preferred hardware* and *Hardware decoding* narrow what is used when it
   is on. "Transcoding settings" below lists every key.
3. **The plan, per session.** Direct Play and Remux never touch a backend at all: nothing is
   re-encoded or decoded. A Transcode plan explains its choice on `PlaybackPlan.AccelerationReasons`:
   which backend it picked and why, or why it is using software (no hardware passed its test, the
   preferred one did not, or the operator's switch is off), and whether that backend also decodes
   *this* session's source codec in hardware (`PlaybackPlan.DecodeAccelerated`). A source codec the
   test could not decode on the device still gets an accelerated encode fed by a software decode, and
   the plan says so rather than leaving it to be inferred from the backend name alone. If the picked
   backend then fails to actually produce a manifest, the encoder itself retries **once**, forced to
   fully software (decode and encode both), never in a loop, and the job that ran is marked
   `FellBackToSoftware` with the reason, so a session running in software despite a plan that named
   VAAPI is explainable rather than a silent discrepancy.

### Transcoding settings

Every knob below is a live setting: change it under **Console → Settings → Playback** and the next
conversion that starts uses it, with no restart. A conversion already running keeps what it started
with. Each one can also be pinned from the deployment by its configuration path, written with double
underscores as an environment variable (`Playback__Transcoding__MaxResolution=1080`,
`Playback__HardwareTranscodingEnabled=false`) in the `cinomni` service's environment (a compose
override file of your own is the least invasive place). A key pinned that way is shown in the Console
but cannot be changed there: the save is refused with `settings.overridden_by_environment` until the
variable is removed. Only environment variables and the command line pin; `appsettings.json` does
not. A pinned value outside the allowed set falls back to the default, and a number outside its range
is clamped to it.

| Key (Console label) | Configuration path | Values | Default | What it does |
|---|---|---|---|---|
| `playback.hardwareTranscodingEnabled` (*Hardware transcoding*) | `Playback:HardwareTranscodingEnabled` | `true`, `false` | `true` | `false` sends every transcode to software, whatever the hardware test found. The escape hatch for a misbehaving driver. |
| `playback.hardwareBackend` (*Preferred hardware*) | `Playback:Transcoding:HardwareBackend` | `auto`, `vaapi`, `qsv`, `nvenc`, `amf` | `auto` | `auto` takes the first backend that passed its test, in the order VAAPI, NVENC, AMF, QSV. A named one is used when it passed and encodes the output codec; otherwise the next in that order, and the plan says the preferred one was not available. |
| `playback.hardwareDecoding` (*Hardware decoding*) | `Playback:Transcoding:HardwareDecoding` | `true`, `false` | `true` | Lets the chosen backend also decode the source when the test decoded that codec on it. Turn it off if a driver decodes some files badly; the encode stays on the GPU. |
| `playback.outputCodec` (*Output codec*) | `Playback:Transcoding:OutputCodec` | `h264`, `hevc-when-supported` | `h264` | `hevc-when-supported` produces HEVC only when the client plays HEVC **and** a hardware backend passed its HEVC encode test; otherwise H.264, and the plan says why. See "Picture subtitles and HEVC output". |
| `playback.encoderPreset` (*Encoder speed*) | `Playback:Transcoding:Preset` | `fastest`, `fast`, `balanced`, `quality` | `fast` | Speed against compression, mapped onto each encoder's own words: x264/x265 `ultrafast`/`veryfast`/`fast`/`medium`, NVENC `p1`/`p3`/`p4`/`p6`, QSV `veryfast`/`faster`/`medium`/`slower`, AMF `speed`/`speed`/`balanced`/`quality`. VAAPI has no preset and ignores it. |
| `playback.videoQuality` (*Video quality*) | `Playback:Transcoding:Quality` | `15`–`40` | `23` | Constant-quality target: CRF for x264/x265, CQ for NVENC, `global_quality` for QSV, QP for VAAPI and AMF. Lower looks better and costs more bandwidth. Used when nothing caps the bitrate; with a cap (this server's or the quality the viewer picked), the hardware encoders switch to a capped variable rate and x264/x265 keep the CRF under a `maxrate`. |
| `playback.maxResolution` (*Maximum resolution*) | `Playback:Transcoding:MaxResolution` | `original`, `2160`, `1440`, `1080`, `720`, `480` | `original` | A server-wide ceiling on picture height. A file taller than it is **converted for every viewer**, even one whose device would play it directly. That is useful on a weak CPU or a slow uplink, expensive otherwise. Any other transcode is also scaled to fit under it. |
| `playback.maxBitrateKbps` (*Maximum bitrate*) | `Playback:Transcoding:MaxBitrateKbps` | `0`–`200000` | `0` (no limit) | A file whose measured bitrate is above it is converted down for every viewer. A file whose bitrate the probe could not measure is not held against it. |
| `playback.encoderThreads` (*Software encoder threads*) | `Playback:Transcoding:Threads` | `0`–`64` | `0` (FFmpeg decides) | Software encoders only. Lower it to leave CPU for the rest of the machine; it bounds the encoder, not a software decode. |
| `playback.toneMapping` (*HDR tone mapping*) | `Playback:Transcoding:ToneMapping` | `true`, `false` | `true` | Brings an HDR source (HDR10, HDR10+, HLG, Dolby Vision) to SDR **when it is being transcoded anyway**. It never causes a transcode on its own. Needs `zscale` in the FFmpeg build; see "HDR and Dolby Vision". |
| `playback.toneMapAlgorithm` (*Tone mapping curve*) | `Playback:Transcoding:ToneMapAlgorithm` | `hable`, `reinhard`, `mobius`, `bt2390` | `hable` | The curve that brings HDR highlights into SDR's range. |
| `playback.maxAudioChannels` (*Maximum audio channels*) | `Playback:Transcoding:MaxAudioChannels` | `original`, `6`, `2` | `original` | Surround above it is folded down in a transcode. It does not cause a conversion by itself. |
| `playback.audioBitrateKbps` (*Audio bitrate*) | `Playback:Transcoding:AudioBitrateKbps` | `64`–`640` | `192` | The bitrate of re-encoded audio. Copied audio is untouched. |
| `playback.burnInImageSubtitles` (*Burn in picture subtitles*) | `Playback:Transcoding:BurnInImageSubtitles` | `true`, `false` | `true` | Whether a picture subtitle (PGS, VobSub) the viewer picks is drawn into the video. Off, such a subtitle cannot be shown in a browser at all. See "Picture subtitles and HEVC output". |

A few settings are deployment-only, read once at start, and are **not** in the Console:

| Configuration path | Default | Meaning |
|---|---|---|
| `Playback:Ffmpeg:BinaryPath` | `ffmpeg` | The FFmpeg that transcodes. The hardware test runs this same binary, so it answers for the hardware the encoder actually gets. |
| `Playback:Ffmpeg:ProbeBinaryPath` | `Import:Ffprobe:BinaryPath`, else `ffprobe` | The ffprobe Playback uses. Unset, it is the one Import uses. |
| `Playback:HardwareAcceleration:DevicePath` | `/dev/dri/renderD128` | Linux only: the render node VAAPI and QSV are tested and run on. |
| `Playback:HardwareAcceleration:NvidiaDevicePath` | `/dev/nvidiactl` | Linux only: the node whose presence says an NVIDIA GPU was granted to the container. Without it NVENC is not even tested. |
| `Playback:TranscodeRoot` | `/data/transcodes` | Where conversions are written ("Resource limits" in section 3). |

### The hardware test

Listing an encoder proves nothing about the machine: every common FFmpeg build carries `h264_nvenc`,
GPU or no GPU. So the test goes further, in three steps, each FFmpeg run bounded to ten seconds:

1. **Read the build.** `ffmpeg -version`, `-hwaccels`, `-encoders` and `-filters`. From these it
   narrows the candidates to what this platform can have. On **Linux**, every candidate also needs
   its device: VAAPI and QSV the render node (`Playback:HardwareAcceleration:DevicePath`), NVENC the
   NVIDIA control node (`/dev/nvidiactl`, which nvidia-container-toolkit creates only in a container
   that was granted a GPU). On **Windows** there are no device nodes to look at, so NVENC, QSV and AMF
   are each a candidate when the build lists `h264_nvenc`, `h264_qsv` or `h264_amf`, and the test
   decides. AMF is never a candidate on Linux, VAAPI never on Windows.
2. **Encode.** Each candidate encodes five frames of a generated picture with its H.264 encoder and,
   where the build has it, its HEVC one, and throws them away. A backend is usable only if its H.264
   encode worked; it produces HEVC only if its HEVC encode worked too.
3. **Decode.** A short H.264 clip and a short HEVC clip are generated in software (the HEVC one needs
   `libx265` in the build) in a temporary directory, and each usable backend decodes them *through the
   device's own pipeline*, keeping the frame on the GPU, so a driver that silently falls back to a
   software decode fails the test instead of passing it. Hardware decode is therefore only ever used
   for **H.264 and HEVC sources**; any other source codec decodes in software and feeds the hardware
   encoder.

It also reads three facts off the build's own listings: `zscale` and `tonemap` (HDR tone mapping),
`overlay` (picture-subtitle burn-in) and `libx265` (software HEVC, which the HEVC decode test needs
for its sample; the planner never picks software HEVC for a stream, being too slow for a viewer who
is waiting).

**Where to read it.** Console → Settings → Playback shows a **Transcoding hardware** panel: each
backend that passed with the codecs it encodes and decodes (or "decodes in software"), whether HDR
tone mapping, subtitle burn-in and software HEVC are available, and the platform, FFmpeg version and
time of the test. **Show test details** lists every test that ran (backend, encode or decode, codec),
marked Passed or Failed, and for a failure FFmpeg's own last line of complaint, cut to 240
characters, with the temporary sample path shown as `<sample>`. The same report is
`GET /api/playback/hardware`; both are administrator-only, because they name the platform and the
FFmpeg build.

**Running it again.** **Run hardware test** (`POST /api/playback/hardware/probe`) repeats the whole
test and puts its result in effect at once for every conversion that starts afterwards, with no restart.
Only one runs at a time: a second request while one is running gets `409 playback.probe_running`.
The result lives in memory, so a restart always tests again from scratch. Run it after anything
changes underneath a running server: a driver installed or updated, a GPU freed by another program,
a device permission fixed on the host. A change to the container itself (a different device passed
through, `CINOMNI_RENDER_GID`, an overlay applied) recreates the container, and the start tests it
anyway. Changing `Playback:Ffmpeg:BinaryPath` or the device paths needs a restart; they are read at
start.

**Reading a failure.**

- **No tests listed at all** means nothing was a candidate. On Linux, the device node is missing in
  the container (no overlay, the wrong `DevicePath`, or no GPU granted to it); on Windows, this FFmpeg
  build carries none of `h264_nvenc`, `h264_qsv`, `h264_amf`.
- **An encode test failed** is the one that matters: that backend is not used at all. A permission
  error on the render node is `CINOMNI_RENDER_GID` (see `docker-compose.hwaccel.yml` below); a
  driver that cannot be initialised or loaded points at the driver package, the toolkit, or a GPU
  generation that driver does not cover. The line is FFmpeg's, verbatim, so search for it as such.
- **Only the HEVC encode failed**: the GPU encodes H.264 and not HEVC, which is common on older generations.
  H.264 output still uses it, and `hevc-when-supported` quietly stays H.264.
- **Only a decode test failed**: the backend still encodes, fed by a software decode for that codec.
  Heavier on the CPU (noticeably so for 4K HEVC), but correct.

### Which setup for which GPU

| Where the server runs | GPU | How | Backend | Notes |
|---|---|---|---|---|
| Linux, Docker (the supported deployment) | AMD | `docker-compose.hwaccel.yml`, `CINOMNI_RENDER_GID` set | VAAPI, through Mesa's `radeonsi` driver (`mesa-va-drivers`) | HEVC encode depends on the GPU generation; the test says which. QSV is Intel-only: if the build lists it, its test fails here, harmlessly. |
| Linux, Docker | Intel | `docker-compose.hwaccel.yml`, `CINOMNI_RENDER_GID` set | VAAPI or QSV, through Intel's `iHD` driver (`intel-media-va-driver-non-free`, Broadwell and newer) | Both run on the same render node. `auto` takes VAAPI first, which FFmpeg itself recommends on Linux; set *Preferred hardware* to `qsv` to use QSV instead, when it passed. |
| Linux, Docker | NVIDIA | `docker-compose.nvidia.yml`, with nvidia-container-toolkit installed on the host | NVENC; decode through CUDA (NVDEC) | No image rebuild. The container must actually be granted the GPU, or `/dev/nvidiactl` is absent and NVENC is not tested. |
| Windows, native (outside the MVP; see below) | AMD | The Host run natively with an FFmpeg build that has AMF | AMF; decode through D3D11VA | Needs a current AMD driver. |
| Windows, native | Intel | Same, with a build that has QSV | QSV | Needs a current Intel graphics driver. |
| Windows, native | NVIDIA | Same, with a build that has NVENC | NVENC; decode through CUDA | Needs a current NVIDIA driver. |
| Windows, Docker Desktop | any | The packaged image, as on Linux | **None: software only** | Docker Desktop runs Linux containers in a WSL 2 VM that exposes no `/dev/dri`, and whose GPU paravirtualisation (`/dev/dxg`) is none of the devices the image's VAAPI, QSV or NVENC paths use. Do not apply either overlay there. Run the Host natively to use the GPU. |

### `docker-compose.hwaccel.yml`: VAAPI and QSV

Builds the `runtime-hwaccel` target (the base image plus `mesa-va-drivers` and
`intel-media-va-driver-non-free`) instead of the base `runtime` one, and passes `/dev/dri` through as
a device. Two things it needs from you that it cannot default safely:

- **`CINOMNI_RENDER_GID`**, in `.env`, no default. The container runs as a fixed non-root uid
  (`CINOMNI_UID`) that belongs to no group on the host's render device by default; without the host's
  actual `render` group id, the device exists inside the container and every open on it still fails
  with `EACCES`. Find it with `getent group render`.
- **The right device**, if the host has more than one GPU. The default tested and used is
  `/dev/dri/renderD128`; point at another with `Playback__HardwareAcceleration__DevicePath` in the
  `cinomni` service's environment (the same key the test, an actual VAAPI encode, and an accelerated
  VAAPI decode all read).

### `docker-compose.nvidia.yml`: NVENC

No image rebuild: the base image's `ffmpeg` already carries `h264_nvenc`. Only the BSD-licensed,
header-only `ffnvcodec` headers were ever needed to compile that in, never the proprietary driver.
What this overlay does is ask Docker for the GPU, through the Compose device-reservation syntax
[nvidia-container-toolkit](https://github.com/NVIDIA/nvidia-container-toolkit) implements. The
toolkit itself has to already be installed and configured on the host; the overlay does not install
it. Confirm the daemon actually sees the GPU before applying it:

```bash
docker run --rm --gpus all ubuntu:24.04 nvidia-smi
```

### Running the server natively on Windows

> **Outside the supported deployment.** The MVP is one Linux amd64 node running the packaged image.
> Running the Host natively on Windows exists for one reason (a GPU that Docker Desktop cannot pass
> to a Linux container), and it is **not verified in CI** or by any drill in this document. The
> other moving parts stay Linux- and Docker-oriented: the torrent sidecar and the VPN overlay have no
> Windows build.

The usual shape is PostgreSQL (and, if you use it, the sidecar) in Docker, and the Host as a native
process. What the Host needs:

- **The .NET 10 runtime** (ASP.NET Core), and the Host built from this repository (`dotnet publish
  src/Host/Cinomni.Host -c Release`), with the web client built (`npm ci && npm run build` in `web/`)
  and `web/dist` copied into the published output's `wwwroot`, which is what the image does.
- **A PostgreSQL 16 it can reach.** The production compose file publishes no database port, on
  purpose; give the database a port bound to loopback deliberately, or run one of your own.
- **An FFmpeg build that has the hardware encoders and the filters**: AMF, QSV and NVENC, plus
  `zscale` (HDR tone mapping) and `libx265` (the HEVC decode test's sample). A "full" Windows build
  from one of the builds linked from ffmpeg.org usually carries all of them; verify rather than
  assume:

  ```powershell
  ffmpeg -hide_banner -encoders | findstr "amf qsv nvenc libx265"
  ffmpeg -hide_banner -filters  | findstr "zscale tonemap overlay"
  ```

  A build without `zscale` plays HDR washed out; one without `libx265` never tests HEVC decode. The
  Console's hardware panel says which is missing.
- **Configuration**, as environment variables (or the command line), with Windows paths:

  ```powershell
  $env:ConnectionStrings__Cinomni       = "Host=127.0.0.1;Port=5432;Database=cinomni;Username=cinomni;Password='...'"
  $env:Playback__Ffmpeg__BinaryPath      = "C:\Tools\ffmpeg\bin\ffmpeg.exe"
  $env:Playback__Ffmpeg__ProbeBinaryPath = "C:\Tools\ffmpeg\bin\ffprobe.exe"
  $env:Import__Ffprobe__BinaryPath       = "C:\Tools\ffmpeg\bin\ffprobe.exe"
  $env:Playback__TranscodeRoot           = "D:\Cinomni\transcodes"
  $env:Import__LibraryRoot               = "D:\Media"
  $env:Backup__Root                      = "D:\Cinomni\backups"
  $env:Backup__PgDumpPath                = "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe"
  ```

  Every root in `appsettings.json` defaults to a `/data/...` path that means nothing on Windows, so
  set each one. The same rules as the image apply: the transcode root is a directory of its own that
  Cinomni deletes in, and must not contain or sit inside the library or the staging area; the backup
  root must not overlap the media roots. `Playback:HardwareAcceleration:*` is Linux-only and can be
  left alone.

**Downloads do not cross that boundary.** The staging path is sent to the sidecar as its save path
and reopened by the Host afterwards, verbatim (section 4). A Windows Host and a Linux sidecar cannot
agree on one string for the same directory, and hardlinking across them is out of the question. A
native Windows Host is a practical way to *play* a library with the GPU; acquiring through the
sidecar from it is not a configuration this project supports.

Once it is up, **Run hardware test** in the Console. The panel's platform line should name Windows,
and the backend your GPU has should be listed as passed.

### Where the frame travels

A transcode moves each frame one of two ways, and the choice decides what it costs:

- **The device pipeline**: decode, scale and encode all on the GPU, the frame never copied to system
  memory. Only VAAPI and NVENC have it, and only when hardware decode is in use for this source and
  nothing needs the frame in system memory.
- **System-memory filtering**: everything else. Tone mapping and subtitle burn-in both need the frame
  in system memory, and QSV and AMF always filter there in this pipeline. **Decode stays on the GPU**
  wherever it can (FFmpeg downloads each decoded frame), because decoding is the expensive half of a
  4K HEVC source; the frame is filtered on the CPU and handed back to the encoder (uploaded again, for
  VAAPI). So an HDR film converted on an Intel or AMD GPU still decodes and encodes on the GPU, and
  spends CPU only on the filtering in between. That filtering scales the picture down *first* and
  tone-maps the smaller frame after, which is about twice as fast as the reverse for a 4K source going to
  1080p.

### HDR and Dolby Vision

- **Tone mapping needs `zscale`** (and `tonemap`) in the FFmpeg build. Check the packaged image's
  build in the Console like any other; a native build must be checked too. Without them an HDR source is
  still converted when it has to be, and plays with washed-out colours, and the plan says why. The
  Console panel's *HDR tone mapping* badge is the quickest check.
- **Tone mapping runs only in a transcode.** An HDR file a device plays directly, or after a remux,
  reaches it untouched: an HDR screen shows it as intended.
- **Dolby Vision profile 5** carries no HDR10 base layer (its colour lives in Dolby's own format,
  which the `zscale` path does not interpret), so a transcode of it can show wrong colours, typically
  a green or purple cast. **Profiles 7 and 8** carry an HDR10 (or HLG) base layer and tone-map
  correctly. The import records a Dolby Vision stream as `DoVi` without the profile, so which one a
  file is has to be read with `ffprobe` or `mediainfo`.
- **4K HDR on the CPU does not keep up at the defaults.** Measured on the packaged container with
  `CINOMNI_CPUS=4.0` and `CINOMNI_MEMORY=2g`, a 4K HEVC 10-bit Dolby Vision source decoded in
  software at about 0.73× real time on its own, and at about 0.65× with scaling, tone mapping and x264
  encoding added. That is slower than playback, so the viewer buffers. A software decode of 4K HEVC also
  needs more memory than the 2 GB default comfortably allows. For CPU-only 4K/HDR transcoding, raise
  both (`CINOMNI_CPUS` to 8–16 and `CINOMNI_MEMORY=6g`) or use a hardware backend, which takes the
  decode and the encode off the CPU and leaves it only the filtering.

### Picture subtitles and HEVC output

**Burn-in always converts.** A browser cannot draw PGS or VobSub subtitles, so when a viewer picks one
the plan becomes a transcode, even for a file the device would otherwise play directly, and the
subtitle is drawn over the picture at the source's own size (FFmpeg's `overlay` filter) before
scaling and tone mapping. It takes the frame through system memory, so it never uses the device
pipeline, and it costs a full conversion slot (`CINOMNI_MAX_TRANSCODES`) for as long as it plays.
With *Burn in picture subtitles* off, or a build without `overlay`, the stream plays without them and
the plan says so. Text subtitles (SRT, ASS, WebVTT) are unaffected: they never cause a conversion.

**HEVC output needs two things at once**: a client that says it plays HEVC, and a hardware backend
whose HEVC encode passed its test. Either missing, the conversion is H.264 and the plan says which.
Software x265 is never used for a stream. When it applies, HEVC needs roughly 40% less bandwidth than
H.264 at the same quality, which matters on a slow uplink and little on a home network. Browsers
play HEVC only from **fragmented MP4 HLS segments** tagged `hvc1`, so an HEVC stream (converted, or an
HEVC source copied through a remux) is written as `init.mp4` plus `.m4s` segments rather than MPEG-TS
`.ts`; an H.264 stream keeps `.ts`. Nothing to configure, but a proxy or cache in front of Cinomni
should pass both.

### What neither overlay does

- **Guarantee decode acceleration.** Both overlays make the *encode* half unconditional once a backend
  passes its test; decode is opportunistic, per session, and depends on the same test also having
  decoded that source's own codec on the device (only H.264 and HEVC are tested; anything else, or a
  codec the installed driver does not cover, plans an accelerated encode fed by a software decode
  instead, explained on `PlaybackPlan.DecodeAccelerated`, never silent). Section 11 records this as
  a standing limitation of what a driver package can be relied on to cover, not specific to either
  overlay.
- **QSV on hardware the `intel-media-va-driver-non-free` package does not cover**, or any Intel
  generation before Broadwell (2014). The test reports nothing usable rather than a broken state, and
  a session plans and runs in software exactly as it would with no overlay applied.
- **Anything for a GPU neither overlay targets.** No overlay here reaches into a container that was
  not asked for one: `docker-compose.hwaccel.yml` never touches `deploy.resources`, and
  `docker-compose.nvidia.yml` never touches `/dev/dri`.
- **Anything on Docker Desktop.** See the table above: software only there.

### Not verified against real hardware

Neither overlay, nor the native Windows backends, has been run against a real VAAPI, QSV, AMF or
NVDEC/NVENC device in this project's CI: there is no such hardware in the environment they were
written in. They are built from FFmpeg's own documented hardware pipelines, encode-only and
full-hardware alike (<https://trac.ffmpeg.org/wiki/Hardware/VAAPI>), from Ubuntu's package contents,
and from the toolkit's own documented Compose integration, not from an observed outcome, on the same
footing the VPN overlay's drop drill (section 8) started from before anyone ran it. The hardware test
exists so that this matters less: a backend that does not behave as documented here fails its test
and is not used. If a GPU still does not do what you expect, the Console's test details, the startup
log's `Hardware transcoding:` line, and a session's `PlaybackPlan.AccelerationReasons` /
`PlaybackPlan.DecodeAccelerated` / `TranscodeJob.FellBackToSoftware` are where to look first. All
are designed so the failure mode is "this installation transcodes in software," never a failed start
or a failed session.

---

## 10. Health, logs and troubleshooting

**The probes** are anonymous, so an orchestrator needs no credentials, and because they are, they
say nothing but a status and a fixed check name. No path, no address, no exception, no connection
string.

| Path | Question it answers | When it fails |
|---|---|---|
| `/health/live` | Is this process alive? | Only if it cannot answer at all. It runs no check, deliberately: a probe that pinged the database would fail during a database restart, and restarting Cinomni does not fix a database. This is what the container `HEALTHCHECK` calls, so an unhealthy container means a restart is worth trying. |
| `/health/ready` | Is it worth sending requests here? | `503` when PostgreSQL is unreachable. `200` with `"status":"Degraded"` when the sidecar is down or a storage root is low: a reduced installation still browses, plays and serves its interface, and taking it out of rotation would turn a partial outage into a total one. Point a load balancer here. |
| `/health` | The same as `/health/ready`, under its original name. | Unchanged, permanently. It is what the development web server proxies and what `host-smoke.sh` calls. |

```json
{"status":"Degraded","checks":[{"name":"library-storage","status":"Healthy"},{"name":"postgres","status":"Healthy"},{"name":"sidecar","status":"Degraded"},{"name":"transcode-storage","status":"Healthy"}]}
```

Checks are `postgres`, `sidecar`, `library-storage` and `transcode-storage`. A storage root reports
`Degraded` below 5 GiB free and `Unhealthy` if it has vanished, which is what a missing mount looks
like; a root that stops answering at all (the usual sign of a stale NAS mount) reports `Degraded`
within a minute. The free space is read on a timer inside the application and never while a probe is
being answered, so a wedged mount can never stop `/health` from replying.

Neither the free byte count nor the path is in the answer, and no description, exception or duration
ever is. What an anonymous caller does learn is the list of check names and each one's status: that
this installation has a torrent sidecar, for instance, and whether it is currently up. The image
binds to loopback by default; if you publish it through a reverse proxy, decide deliberately whether
to forward `/health` or to expose only `/health/live` and `/health/ready`.

Startup is the one thing the split does not cover: the schemas are applied before the server binds,
so during a long upgrade the probes are refused rather than answered. That is what the
`HEALTHCHECK`'s two-minute start period is for.

**Telemetry** is off until you set `CINOMNI_OTLP_ENDPOINT` to a collector you run. With it empty no
exporter is registered and nothing leaves the process. With it set, Cinomni exports over OTLP:

- **traces**: one trace per acquisition, carried across the outbox and the command queue, so
  "search → decide → download → import" is one story rather than five unrelated ones;
- **metrics**: outbox backlog and relay lag, command queue depth by state, job runs, refusals by
  the SSRF guard, per-indexer search time and failures, download rates and stalls, tunnel holds,
  import duration by outcome, and transcodes running on this node;
- **log records reduced to their message template** (`Library root {Root} is not accessible.`),
  with the level, the logger's category and the trace and span id of the work that produced them.
  Correlation is on in every installation, exporter or not, so a log line can be tied to its trace
  from the console too.

The values a log message interpolates never leave this node, and neither does an exception's message
or stack trace. A log line in this product routinely names a library root, a media path, an info hash
or a search term, and an FFmpeg failure quotes the file it was handed, so a log record is held to
exactly the same rule as a span attribute, and only the template, which is a constant written in the
source, is exported. Read the values with `docker compose logs`, on the machine that produced them.

There is no scrape endpoint and no Prometheus exporter in the image. Run an OpenTelemetry Collector
and let it expose one; that also keeps a metrics surface off the port that serves your library.

What is exported is treated as public, and it is filtered in code rather than promised: no secret,
api key, indexer URL or provider host, info hash, path, title, search term, account or session id
ever becomes a span attribute, a metric label or a log record. An outgoing request's span keeps the
path and nothing else: not the query string, because that is where the key is, and not the host,
because it names the private tracker, metadata provider or webhook you configured. The built-in
`System.Net.Http` metrics keep the method, scheme, status and error kind and lose the host with them.
*Which* provider was slow is answered by Cinomni's own per-indexer instruments, which carry the name
you gave the indexer.

One thing to know if you publish Cinomni beyond your own network: ASP.NET Core adopts the W3C
`traceparent` and `tracestate` headers of an incoming request, so a caller can put your spans on a
trace id of their choosing and have them sampled regardless of `Observability:Traces:SampleRatio`.
Cinomni refuses to carry a `tracestate` that is not well formed, but the sampling decision is still
the caller's. Strip both headers at the reverse proxy if that matters to you.

**Logs** go to the container's standard output:

```bash
docker compose logs -f cinomni
docker compose logs -f torrent-sidecar
```

| Symptom | What it means |
|---|---|
| `Storage root '/data/library' (configuration 'Import:LibraryRoot') is not writable by this process` and the container exits | The host directory is not owned by `CINOMNI_UID`, or the filesystem behind it is read-only or full. This check runs before anything else precisely so this is one line at start-up instead of imports that defer forever: nothing has been migrated, nothing is serving, and the exit code is `78` (`EX_CONFIG`, "the configuration is wrong") rather than a stack trace and a process killed by a signal, so `docker ps -a` shows a refusal to start and not a crash. It proves the account can write, and no more: an absent root is created, so a bind mount whose backing filesystem never came up looks fine to it and quietly starts an empty library on the wrong disk. If your media lives on a NAS or an external disk, make sure it is mounted before Cinomni starts. |
| The same message for `Playback:TranscodeRoot`, as a **warning**, and the application keeps running | Transcoding is scratch and Cinomni will not take the whole installation down for it. Direct play, the library and the interface work; a transcode fails for its own session. The usual cause after changing `CINOMNI_UID` is a transcode volume created for the previous account; section 2 has the one-line remedy. |
| `A hardlink from the download staging root (configuration 'Downloads:Sidecar:StagingPath') into the library root (configuration 'Import:LibraryRoot') is not possible on this installation: link() failed with EXDEV (18) ...` | The storage contract in section 4 is broken, and every import will copy instead of hardlinking: twice the disk, and the library keeps its copy when you remove the download. `EXDEV` means the two roots are on **two filesystems**: with the packaged compose file, `/data/library` or `/data/downloads` was pointed somewhere else, or a volume was mounted over one of them. `EPERM` means a **uid mismatch** instead: the two containers are not running as the same account, and `fs.protected_hardlinks` refuses the link. A warning and not a crash, because copies still import, but this is the one line that says an installation is silently using twice the disk it needs. Docker's volumes make the device *number* useless for this, so the check attempts a real link rather than comparing `stat`. |
| `Import job ... copied at least one file into the library instead of hardlinking it` | The same fault, observed while importing rather than at boot, once per job. The per-file rows in `import.file_operations` record `Copy` for exactly the files it happened to, so the job's own trail says what was done to each one. |
| `Import job ... found at least one file already in the library from an earlier attempt that was interrupted ... recorded as Unknown` | Not a fault, and nothing was lost: a job resumed after a restart found its file already in place and the filesystem would not say whether it shares its bytes with the download or is a second copy of them (section 4). The affected rows in `import.file_operations` carry `Unknown` rather than a guess. The one thing it costs you is certainty about that file: before removing the download, check whether the library copy survives it, because the installation cannot tell you. It takes a restart in the middle of an import *and* a filesystem read that fails, so it is rare; a file whose row was written is never asked again. |
| The sidecar exits with `data root /data/downloads is not writable by uid 1654` | Same cause on the other side, and here it is fatal on purpose: a sidecar that cannot write is a sidecar whose every download fails, and it must not report healthy while doing it. |
| `TMDB is disabled: no API key is configured` (or `TheTVDB is disabled`), or `Optional credential 'Subtitles:Provider:ApiKey' is not set` | Informational. That provider is unavailable; everything else works. Only the key's name is ever logged. |
| `Hardware transcoding: no VAAPI/NVENC/QSV/AMF backend passed its test at startup` | Expected without an overlay, and on Docker Desktop. With one applied, open Console → Settings → Playback → *Transcoding hardware* → *Show test details*: no tests listed means the device never reached the container, and a failed test carries FFmpeg's own reason. Section 9, "The hardware test". |
| `External tool 'ffmpeg' ... was not found` | Should not happen with the packaged image; it means the runtime layer was modified. Transcoding will report a failure instead of running. |
| `External tool 'pg_dump' (configuration 'Backup:PgDumpPath') was not found` | Same cause, and it means **this installation is not backing itself up**. Everything else works. Reinstall `postgresql-client-16` in the image, or point `Backup:PgDumpPath` at a client of the server's major version. The failed run is also recorded in `operations.backup_run`, not only in this log line. |
| `Backup:Root ('...') is inside the storage root '...'` and the container exits | A dump is a credential store and the media roots are served to clients; the two may not overlap in either direction. This compares the paths *inside* the container, so with the packaged compose file it means one of `Backup__Root`, `Import__LibraryRoot`, `Downloads__Sidecar__StagingPath` or `Playback__TranscodeRoot` was overridden. Changing `CINOMNI_BACKUP_DIR` cannot produce or fix it: that is a host path, and the check never sees it. |
| `The backup client reports 'pg_dump (PostgreSQL) 17.x' and the server is 16.x` | The dump is valid; restoring it is not, because `pg_restore` writes a preamble the older server rejects. Section 6, last paragraph before the drill. |
| The restore script says `REFUSED: ... the database could not be asked whether anything is still attached`, naming `postgres` | The restoring role cannot connect to the maintenance database, which **every** run needs and not only `--recreate`; that is where the "is the application stopped" question is asked from. Pass `--maintenance-db` naming a database the role can connect to; any of them answers it. Section 6, "It needs a maintenance database, on every run". |
| The restore script says `REFUSED: '<name>' could not be inspected` | Whether the target is empty could not be established, so it is not assumed to be. Usually the database does not exist yet (create it, or pass `--recreate`), or this role cannot connect to it. Never a reason to skip the check: a database nobody could inspect is not an empty one. |
| `cinomni` never reaches `healthy` | Look for a migration error in the logs. A failed migration is a crash loop by design: the application never serves traffic against a schema it could not bring up to date. |
| `Using an in-memory repository. Keys will not be persisted` / `Using an ephemeral key repository` | Harmless today, and expected: the container's filesystem is read-only apart from `/data`, so ASP.NET Core's data protection finds nowhere to keep its keys. Nothing in Cinomni uses it: sessions are opaque tokens stored hashed in the database, not encrypted cookies. Revisit if that ever changes. |
| `Torrent egress: no tunnel is configured ...` | Informational, and the expected first line without the overlay. It states, at every start, that torrent traffic leaves over your own connection. |
| `Torrent egress: bound to tunnel device 'tun0' under the PauseAndAlert policy, which is weaker than the Block default` | You chose a weaker policy. It is a warning at every start on purpose: an installation running below the default must never look like one that is not. |
| The sidecar exits with `tunnel egress cannot be verified on device tun0 ... SIDECAR_TUNNEL_POLICY is 'block'` | The tunnel is not there and the policy says do not run without it. Usually the tunnel container failing its own health probe; read its log first. This is the intended behaviour, not a bug: a sidecar that started here would download over your own connection. |
| `Downloads are on hold: the VPN tunnel is not carrying their traffic` | The guard did what it is for. `GET /api/downloads/tunnel` carries the observed reason; the table below is the whole vocabulary. Transfers resume by themselves. |
| `The sidecar could not report its egress path (Unimplemented)` | A sidecar image older than the application. Rebuild both (`docker compose build`) and start the sidecar first. Under `block` this holds the downloads, deliberately: an old sidecar is also one that never learned to bind to the tunnel. |
| `409 downloads.network_held` when resuming a download | Under `block` a held download is released by the tunnel coming back and by nothing else. Under `pause-and-alert` you may override it with `POST /api/downloads/{id}/resume?force=true`, and the override is written to the task's history. |

**Every egress reason**, as `GET /api/downloads/tunnel` reports it. This is the whole vocabulary: a
value not listed here is a defect, not a state.

| `reason` | What was observed, and where to look |
|---|---|
| `tunnel-egress-verified` | All the facts hold. Nothing to do. |
| `not-yet-observed` | No observation has been taken yet, as in a freshly started installation. It is never a verification. |
| `tunnel-guard-disabled` | The sidecar has no `SIDECAR_TUNNEL_DEVICE`. Torrent traffic leaves over your own connection. Expected without the overlay; a misapplied overlay otherwise. |
| `tunnel-device-missing` | The interface does not exist. The tunnel never came up; read the tunnel container's own log first. |
| `tunnel-device-has-no-address` | The interface exists and carries no address. Usually transient: the tunnel process is up and the provider has not handed out an address yet. Persisting means the provider handshake is failing. |
| `default-route-not-via-tunnel` | Up, but not routed. What a restarted tunnel looks like for the seconds before it reinstalls its routes; persisting means something else owns the default route. |
| `egress-identity-not-the-tunnel` | Routed and still leaking: the kernel would put another address on the next packet. A policy route or a second interface. This is the leak the first two checks miss. |
| `ipv6-egress-not-the-tunnel` | The IPv4 half is correct and IPv6 would leave with an address the tunnel does not hold. Either give the tunnel IPv6 or remove IPv6 egress from the container's namespace. |
| `no-egress-route` | Nothing can leave at all, including the probe. What a correctly sealed container looks like while its tunnel is down. |
| `tunnel-guard-observation-failed` | The sidecar's own guard could not take the observation: a syscall failed, typically under file-descriptor pressure. Read the sidecar log; it names the error. |
| `sidecar-unreachable` | Nobody could be asked: the sidecar is down, too old to answer, or refusing the control credential. Not the same as fine. |
| `tunnel-observation-stale` | The sidecar answered with an observation older than three poll intervals. Its guard has stopped observing, or the two containers' clocks disagree. |
| `tunnel-policy-divergent` | The sidecar reports enforcing a weaker policy than `CINOMNI_TUNNEL_LOSS_POLICY`. Both halves read the same variable in the shipped overlay, so this is a hand-edited or partially applied deployment. |
| `tunnel-device-divergent` | The sidecar reports a different interface than `CINOMNI_TUNNEL_DEVICE`. Same cause, same fix: one variable feeds both. |
| `tunnel-guard-removed` | The tunnel was taken out of the configuration while downloads were held, and the holds were released. Recorded so "why did these stop and start again" has an answer. |

Stopping is graceful: the application gets 60 seconds to finish in-flight work before it is killed.

```bash
docker compose down          # stop, keep the data
docker compose down -v       # stop and delete the database volume as well
```

---

## 11. Not supported

Deliberately, and matching the MVP scope in [ROADMAP.md](./ROADMAP.md):

- **No TLS in the image.** It serves plain HTTP on 8080. Put a reverse proxy in front for anything
  beyond loopback.
- **No reverse-proxy header trust by default.** Unless `CINOMNI_TRUSTED_PROXIES` names the proxy, the
  application ignores `X-Forwarded-For` and `X-Forwarded-Proto`, so behind a proxy it sees the
  proxy's address and its own scheme. Declaring a proxy means believing whatever that proxy writes in
  those headers, so declare only one that sets them itself (section 3, "Publishing beyond loopback").
- **No guaranteed decode acceleration.** VAAPI/NVENC/QSV/AMF (section 9, opt-in) always accelerate
  the *encode* half of a transcode once a backend passes its test. Decode is opportunistic: it only
  accelerates when the hardware test also decoded that session's own source codec on the same
  backend (only H.264 and HEVC are tested), which a driver package or an older GPU generation is not
  guaranteed to provide for every codec. When it does not, the source decodes in software and feeds
  an otherwise-accelerated encode, and the plan says so (`PlaybackPlan.DecodeAccelerated`) rather than
  leaving it to be inferred. Direct Play and Remux never decode or encode at all.
- **No Usenet**, no multi-node or high availability, no Kubernetes, no native mobile or TV apps.
- **No supported Windows deployment.** Running the Host natively on Windows to reach a GPU is
  described in section 9 and is meant for playback only; it is outside the MVP, not verified in CI, and
  the torrent sidecar and the VPN overlay remain Linux-only.
- **One instance.** Migrations and recovery run at startup on the assumption of a single process.
  Do not scale the `cinomni` service.

---

## 12. Base images, tools and licences

Every base image is pinned by digest, so a rebuild always starts from the same bytes. **The layers built
on top are not pinned**: `apt-get install` and `pip install` take whatever the archives serve on the day,
so the package versions in the tables below are what one build on 2026-07-29 produced, not a guarantee
about yours. Read them as provenance, not as a lockfile. Pinning those layers (exact versions and
hashes) is open work.

The base images, verified by pulling each one on 2026-07-29 and reading it, not from memory:

| Image | Digest | Provenance | Licence |
|---|---|---|---|
| `mcr.microsoft.com/dotnet/aspnet:10.0` | `sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7` | Microsoft Container Registry. .NET 10.0.10 runtime on **Ubuntu 24.04 LTS (noble)**, not Debian. Provides the non-root `app` account, uid/gid 1654. | .NET: MIT. Ubuntu base: the distribution's own package licences. |
| `mcr.microsoft.com/dotnet/sdk:10.0` | `sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664` | Microsoft Container Registry. .NET SDK 10.0.302 on Ubuntu 24.04 LTS. Build stage only; nothing from it ships. | MIT (.NET). |
| `node:22-bookworm-slim` | `sha256:6c74791e557ce11fc957704f6d4fe134a7bc8d6f5ca4403205b2966bd488f6b3` | Docker Official Image. Build stage only. | Node.js: MIT. Debian base: the distribution's own package licences. |
| `postgres:16` | `sha256:33f923b05f64ca54ac4401c01126a6b92afe839a0aa0a52bc5aeb5cc958e5f20` | Docker Official Image. | PostgreSQL Licence (permissive, BSD-like). |
| `debian:bookworm-slim` | `sha256:7b140f374b289a7c2befc338f42ebe6441b7ea838a042bbd5acbfca6ec875818` | Docker Official Image. Base of the sidecar image. | The distribution's own package licences. |
| `ghcr.io/flaresolverr/flaresolverr:v3.4.5` | `sha256:4f4e5f759aa3a9a64305e99188ea1db1ec2944a5e7d290d2b089af5f2f6f48e4` | FlareSolverr upstream image, run unmodified on the isolated browser network. | MIT (FlareSolverr); bundled Chromium and packages retain their own licences. |
| `qmcgaw/gluetun:v3.41.2` | `sha256:280809bc6900ed06d6529ad246499103efb31fd80c6327cbfaf90de0b17c3a99` | Docker Hub, from [github.com/qdm12/gluetun](https://github.com/qdm12/gluetun). **Only used by the VPN overlay** (section 8); the default topology never pulls it. Unmodified and run as-is: it is a dependency the way libtorrent is a dependency, and no Cinomni code, configuration or wording is taken from it. Digest resolved from the registry on 2026-07-29 and verified to carry `linux/amd64`. | MIT (the image's own `org.opencontainers.image.licenses` label). It bundles OpenVPN and WireGuard tooling under their own licences, run as processes inside that image. |

Packages installed into the runtime layer, from the Ubuntu 24.04 archive; the versions are what that
day's archive served, and a rebuild may get newer ones:

| Package | Version | Why | Licence |
|---|---|---|---|
| `ffmpeg` | `7:6.1.1-3ubuntu5` | Provides both `ffmpeg` and `ffprobe`, which Playback and Import launch as child processes. | This build reports `--enable-gpl --enable-libx264`, so the binaries are **GPL-2.0-or-later**. Cinomni does not link against FFmpeg (it starts it as a separate process and passes an argument vector), so each component retains its own licence when shipped together. |
| `curl` | `8.5.0-2ubuntu10.11` | The only HTTP client in the runtime image; it exists so the container `HEALTHCHECK` has something to probe `/health/live` with. | curl licence (MIT/X-derivative). |
| `postgresql-client-16` | `16.14-0ubuntu0.24.04.1` (with `libpq5` and `postgresql-client-common`; about 4.8 MB installed) | `pg_dump`, `pg_restore` and `psql`. The scheduled backup runs inside this container, so an image without them is an installation that cannot copy its own database. The major version tracks the `postgres` image in `docker-compose.yml`: a restore needs client and server to match. | PostgreSQL Licence (permissive, BSD-like). |

Packages installed only into the `runtime-hwaccel` target (section 9's `docker-compose.hwaccel.yml`,
never the base `runtime` layer). Unlike the table above, **these have not been verified by pulling and
reading a real build**: there was no VAAPI/QSV hardware available to build and test against, so the
version column says so honestly rather than reporting a number nobody observed:

| Package | Version | Why | Licence |
|---|---|---|---|
| `mesa-va-drivers` | *(not yet verified: no build has been run to record it)* | The open VAAPI backend for AMD GPUs and older Intel iGPUs (the i965 driver). | MIT/X11-style (the Mesa project's own licence). |
| `intel-media-va-driver-non-free` | *(not yet verified: no build has been run to record it)* | Intel's iHD VAAPI driver for Broadwell (2014) and newer, the backend ffmpeg's `h264_qsv` path uses on Linux today. Ubuntu's `multiverse` component; "non-free" names its redistribution terms, not a licence Cinomni's own code takes on. | BSD-3-Clause (the driver's own upstream licence) plus proprietary Intel firmware blobs it may pull in as a dependency; read the package's own copyright file before redistributing a built image. |

NVENC needs no package here at all: the base `runtime` target's `ffmpeg` already carries `h264_nvenc`
(only the BSD-licensed, header-only `ffnvcodec-headers` were needed to compile it in), and
[nvidia-container-toolkit](https://github.com/NVIDIA/nvidia-container-toolkit) (Apache-2.0, installed
and configured on the **host**, never pulled into any Cinomni image) injects the proprietary driver
libraries into the container at start. `docker-compose.nvidia.yml` documents the prerequisite; there is
nothing here to pin because nothing here is Cinomni's to build.

The sidecar image additionally installs `python3`, `python3-libtorrent`, `python3-pip`,
`python3-venv` and `ca-certificates` from Debian, plus `grpcio`, `grpcio-tools` and `protobuf` from
PyPI. None of those are version-constrained either, and the PyPI ones are the weakest link in the chain:
each image build fetches whatever the index currently offers, into the process that holds your download
directory. libtorrent is BSD-3-Clause and is linked as a third-party dependency, never as a source of
Cinomni code.

**Redistribution.** The project publishes these images on GitHub Container Registry, which makes it a
distributor of the binaries they package: Cinomni under AGPL-3.0-or-later, an FFmpeg build under
GPL-2.0-or-later, and the Debian, Ubuntu and upstream packages each base image carries, each under its
own terms. Every published image names its exact source revision in its OCI labels
(`org.opencontainers.image.source` and `.revision`), and the release it belongs to is tagged at that
revision. If you push an image of your own to a registry, you become a distributor of it too, and the
corresponding source and licence notices are yours to provide. Running a build privately does not
trigger a redistribution obligation.
If you run a **modified** Cinomni build for users over a network, AGPL section 13 also requires a
prominent offer of that build's corresponding source to those users. The app shows a source link on
sign-in, playback and normal pages; set `CINOMNI_SOURCE_URL` to the modified source when building.

### Refreshing a pin

A digest pin means a rebuild never silently changes base image, and also that security updates do not
arrive on their own. Refresh deliberately:

```bash
docker pull --platform linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0
docker image inspect mcr.microsoft.com/dotnet/aspnet:10.0 --format '{{index .RepoDigests 0}}'
```

Put the new digest in the `FROM` line, rebuild, and update the table above. Note the codename and the
package versions from the image you actually pulled; this table was written that way and must stay
that way.
