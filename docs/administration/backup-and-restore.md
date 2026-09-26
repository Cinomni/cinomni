# Backup and restore

Cinomni backs up **its database** on its own. It does **not** back up **your media**. You need both.

## What is where

| What | Backed up by | Why it matters |
|---|---|---|
| The database | Cinomni, daily | Accounts, the library index, watch progress, requests, monitoring, the reasons behind every decision, download progress. Most of it cannot be recreated. |
| `data/library` (including `.recycle`) | **You** | Your media files and the subtitles beside them. |
| `.env` | **You** | Passwords and keys, including `CINOMNI_SECRET_KEY`: without it, stored credentials cannot be decrypted. Keep it as secret as the backups. |
| `data/downloads`, transcodes | Nobody | Can be downloaded or regenerated again. |

## Automatic database backups

Once a day Cinomni takes a consistent dump of the whole database into the `cinomni-backups` volume, as
two files: `cinomni-<timestamp>.dump` and a `.manifest.json` describing it (with a checksum and no
credentials). The newest seven are kept.

| `.env` variable | Default | Meaning |
|---|---|---|
| `CINOMNI_BACKUP_DIR` | a Docker volume | A host folder to write the dumps to instead, so you can copy them elsewhere. **Never inside `CINOMNI_DATA_DIR`.** |
| `CINOMNI_BACKUP_KEEP` | 7 | How many to keep. |
| `CINOMNI_BACKUP_INTERVAL` | `1.00:00:00` | How often. |
| `CINOMNI_BACKUP_ENABLED` | `true` | `false` stops the schedule; manual backups still work. |

> **A backup is a credential store.** It is not encrypted and contains webhook URLs, indexer addresses
> (often with an API key in them), password hashes and session data. Store and copy it as carefully as
> `.env`. There is deliberately no way to download a backup from the web interface.

## By hand

Before every upgrade:

```bash
docker compose exec cinomni dotnet Cinomni.Host.dll backup create
docker compose exec cinomni dotnet Cinomni.Host.dll backup list
docker compose exec cinomni dotnet Cinomni.Host.dll backup verify <timestamp>   # re-check the file
docker compose exec cinomni dotnet Cinomni.Host.dll backup check  <timestamp>   # can this build restore it?
```

## Getting backups off the machine

A backup on the disk that failed is not a backup. With the default volume:

```bash
docker run --rm -v cinomni_cinomni-backups:/backups -v "$PWD:/out" alpine \
  sh -c 'cp /backups/cinomni-*.dump /backups/cinomni-*.manifest.json /out/'
```

Or set `CINOMNI_BACKUP_DIR` to a host folder and copy it with the tool you already use.

## Checking that backups run

Every attempt, successful or not, is recorded in the database:

```bash
docker compose exec postgres psql -U cinomni -d cinomni -c \
  "SELECT started_at, outcome, stamp, reason FROM operations.backup_run ORDER BY started_at DESC LIMIT 10;"
```

A failed run is retried within about fifteen minutes.

## Restoring

Restoring replaces the entire database, so it is a deliberate procedure run from the command line,
not a button. In short:

1. `backup check <timestamp>` while Cinomni is still running.
2. `docker compose stop cinomni`.
3. Run the restore script shipped in the image, `/app/scripts/cinomni-restore.sh`, which verifies the
   file and refuses to run while the application is still connected.
4. `docker compose start cinomni` and check `/health`.

Follow the exact commands in
[DEPLOYMENT.md, "Restoring"](../../DEPLOYMENT.md#restoring): they matter.

Afterwards, sign everyone out if the backup is old, and re-import anything acquired after it was
taken; the files are still on disk.

Practise once in a while by restoring into a scratch database; the drill is described in
[DEPLOYMENT.md, "The drill"](../../DEPLOYMENT.md#the-drill).
