# Troubleshooting

## Where to look first

1. **Console → System → Readiness**: which part is unhealthy.
2. **Activity** for a title that does not arrive: its attempts, download and history, each with a
   reason.
3. **Console → Operations → Failed commands**: background work that failed, with the error and a
   **Retry** button.
4. **The logs**:

   ```bash
   docker compose logs -f cinomni
   docker compose logs -f torrent-sidecar
   ```

## Health checks

| Path | Answers |
|---|---|
| `/health/live` | Is the process alive? Used by the container health check. |
| `/health/ready` | Is it worth sending requests? `503` when the database is unreachable; `Degraded` when the torrent engine is down or storage is low. |
| `/health` | Same as `/health/ready`. |

The checks are `postgres`, `sidecar`, `library-storage` and `transcode-storage`. Storage is
*Degraded* below 5 GB free, and *Unhealthy* when the folder has disappeared (typically an unmounted
disk).

## Common problems

| Problem | What to do |
|---|---|
| `docker compose up` refuses to start and names a variable | Set that variable in `.env`: `POSTGRES_PASSWORD`, `CINOMNI_TORRENT_EGRESS` and `CINOMNI_SIDECAR_CONTROL_TOKEN` are required; `VPN_SERVICE_PROVIDER` too with the VPN overlay. |
| The `cinomni` container exits saying a storage root *is not writable* | The data folder does not belong to `CINOMNI_UID` (1654 by default), or its disk is read-only or full. `sudo chown -R 1654:1654 data`, or set `CINOMNI_UID`/`CINOMNI_GID` to the owner. |
| The sidecar exits saying `/data/downloads` is not writable | Same cause, on the downloads folder. |
| `cinomni` never becomes *healthy* | Look for a migration error in `docker compose logs cinomni`. The application does not serve until its database is up to date. |
| Log warns that a hardlink *is not possible* (`EXDEV` or `EPERM`) | Every import will copy. `EXDEV`: library and downloads are on different filesystems. `EPERM`: the two containers run as different users. See [Import and storage](import-and-storage.md). |
| Adding a movie finds nothing / says no metadata provider | Set a TMDB key (and TheTVDB for series) in `.env` or **Console → Settings → Metadata**. |
| Saving an indexer credential or metadata key fails, naming `CINOMNI_SECRET_KEY` | Set `CINOMNI_SECRET_KEY` in `.env` and restart. Cinomni refuses to store credentials unencrypted. |
| Stored credentials show as *not configured* after a change | `CINOMNI_SECRET_KEY` changed or was lost. Put the old key back, or enter the credentials again. |
| Searches return nothing | See [Indexers](indexers.md#when-searches-find-nothing). |
| Downloads are paused with *Network held* | The VPN tunnel is not verified. **Console → System → Torrent egress** shows the reason; downloads resume on their own when it is back. See [Downloads](downloads.md#through-a-vpn). |
| Resuming a download fails with `downloads.network_held` | Under the `block` policy a held download is released only by the tunnel coming back. |
| Transcodes fail after changing `CINOMNI_UID` | The transcode volume belongs to the old user: `docker compose down`, `docker volume rm cinomni_cinomni-transcodes`, start again. |
| *Hardware transcoding: no … backend passed its test* | Expected without a GPU overlay, and on Docker Desktop. Otherwise see [Transcoding](transcoding.md#the-hardware-test). |
| The player says the server (or your account) is converting as many streams as it can | The transcode limits were reached; see [Transcoding](transcoding.md#limits). |
| 4K/HDR buffers | The server converts too slowly: more CPU and memory, or a GPU. |
| Everyone is locked out after too many sign-in attempts behind a proxy | The proxy is not declared, so everyone shares one address. See [Networking](networking.md#tell-cinomni-about-the-proxy). |
| An administrator lost the two-factor device and the recovery codes | See [DEPLOYMENT.md, "If you are locked out of an account"](../../DEPLOYMENT.md#if-you-are-locked-out-of-an-account). |
| Backups fail, naming `pg_dump` | The image was modified; backups are not running. Every attempt is in `operations.backup_run`. |
| Log says *Using an ephemeral key repository* | Harmless and expected. |

The complete list of log messages, and every reason the VPN check can report, is in
[DEPLOYMENT.md §10](../../DEPLOYMENT.md#10-health-logs-and-troubleshooting).

## Asking for help

See [Getting help](../getting-help.md) for what to include in a report.
