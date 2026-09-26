# Settings reference

**Console → Settings** holds the settings that can be changed while Cinomni runs. A change applies
without a restart, except where noted.

Settings that are about the machine rather than the application (passwords, storage paths, ports,
resource limits, the VPN) live in `.env`; see [Installation](../getting-started/installation.md) and
[DEPLOYMENT.md §3](../../DEPLOYMENT.md#3-settings).

## When a setting cannot be changed

A setting shown as **Fixed by environment** has been pinned by an environment variable (or a command
line argument) on the `cinomni` container. The console shows its value but refuses to change it until
the variable is removed. The variable name is the setting's configuration path with `__` between the
parts, for example `Playback__Transcoding__MaxResolution=1080`.

## Catalog

| Setting | Meaning |
|---|---|
| Add trending titles | On: a scheduled job adds TMDB trending movies and series that are not in the catalog yet, **unmonitored**, so nothing is downloaded until you switch a title on or approve a request for it. It never removes a title. The list is under **Console → Trending**. |

## Decision

| Setting | Meaning |
|---|---|
| Evaluation retention | How long release evaluations (the reasons behind every decision) are kept. |
| Purge interval | How often old evaluations are purged. Takes effect after a restart. |

## Import

| Setting | Meaning |
|---|---|
| Movie file names | `ReleaseName` keeps the downloaded name; `TitleYear` names folder and file from the catalog. New imports only. |

## Metadata

| Setting | Meaning |
|---|---|
| TMDB API key | Movie search needs it; series use it alongside TheTVDB and TVmaze. Stored encrypted, never shown. |
| TheTVDB API key | Gives series their seasons and episodes. |
| TheTVDB subscriber PIN | Only for a user-supported TheTVDB key. |
| Classification region | Two-letter country code (`US`, `ES`, `DE`, `GB`) whose age ratings are read. Empty reads none, and disables content ceilings. |
| Snapshot retention | How long old metadata snapshots are kept. |
| Purge interval | How often they are purged. Takes effect after a restart. |

A key set in `.env` wins over one entered here. Keys entered here need `CINOMNI_SECRET_KEY`.

## Monitoring

| Setting | Meaning |
|---|---|
| Search delay after air | How long the automatic search waits after an episode's air time. Zero searches at once. Format `d.hh:mm:ss`. A manual search never waits. |

## Requests

| Setting | Meaning |
|---|---|
| Open requests per account | How many open requests an account may have unless its own setting says otherwise. 0 is no limit (the default). |

## Security

| Setting | Meaning |
|---|---|
| Sign-in attempts per client | How many sign-in or setup attempts one client may make per window (1–1000). |
| Sign-in attempt window | The length of that window, 1 minute to 1 hour. |

Behind a reverse proxy the "client" is only right if the proxy is declared; see
[Networking](networking.md#tell-cinomni-about-the-proxy).

## Subtitles

| Setting | Meaning |
|---|---|
| Wanted languages | Two-letter codes in search order, for example `en, es`. Also applies to titles already in the library. |
| Hearing-impaired only | Only a hearing-impaired subtitle satisfies a language (off: such a subtitle does not). |
| Forced only | Only a forced subtitle satisfies a language (off: a forced one does not). |

## Playback

Every conversion started after a change uses the new value; one already running keeps its settings.
Details and the matching configuration paths are in
[DEPLOYMENT.md §9](../../DEPLOYMENT.md#transcoding-settings).

| Setting | Default | Meaning |
|---|---|---|
| Hardware transcoding | on | Off sends every conversion to the CPU, whatever the hardware test found. |
| Preferred hardware | Automatic | Which GPU backend to use when several passed the test. |
| Hardware decoding | on | Also decode the source on the GPU. Turn off if a driver decodes some files badly. |
| Output codec | H.264 | `HEVC when supported` uses HEVC only when the device plays it and the GPU encodes it. |
| Encoder speed | fast | Faster uses less CPU/GPU; slower looks better at the same bitrate. |
| Video quality (15–40) | 23 | Lower looks better and uses more bandwidth. |
| Maximum resolution | original | Anything taller is converted down **for every viewer**. |
| Maximum bitrate (kbps) | 0 (no limit) | A file above it is converted down for every viewer. |
| Software encoder threads | 0 (automatic) | Lower it to leave CPU for other services. |
| HDR tone mapping | on | Bring HDR and Dolby Vision to SDR when a video is being converted anyway. |
| Tone mapping curve | hable | The curve used for tone mapping. |
| Maximum audio channels | original | Surround above it is folded down when converting. |
| Audio bitrate (kbps) | 192 | Bitrate of re-encoded audio. |
| Burn in picture subtitles | on | Allow PGS/VobSub subtitles to be drawn into the video (forces a conversion). |

The same page has the **Transcoding hardware** panel; see
[Transcoding and hardware](transcoding.md).

## Backup

| Setting | Meaning |
|---|---|
| Backups to keep | How many database backups are kept; older ones are removed after a successful backup. |
| Backup interval | How often a backup is taken. |

## Operations

Retention of internal records: the outbox of processed events, completed and failed commands, the
purge batch size and how often the purge runs (after a restart). The defaults suit almost every
installation; **Console → Operations → Retention** shows the policy in effect.
