# Upgrading

Cinomni is **alpha**: a new version can need a step of its own, and there is no way back to an older
version once the database has been upgraded. Take the steps below every time.

1. **Read the release notes** for every version between yours and the new one, in
   [CHANGELOG.md](../../CHANGELOG.md) or on the
   [releases page](https://github.com/Cinomni/cinomni/releases). Follow any *Upgrade notes*.
2. **Take a backup:**

   ```bash
   docker compose exec cinomni dotnet Cinomni.Host.dll backup create
   ```

3. **Point to the new version.** Either set `CINOMNI_IMAGE_TAG` in `.env` to the new version, or
   replace `docker-compose.yml` with the one attached to the new release (it already names its
   version). With a clone of the repository, `git pull` does the latter.
4. **Pull and restart**, with the same `-f` overlays you normally use:

   ```bash
   docker compose pull
   docker compose up -d
   docker compose ps      # wait for "healthy"
   ```

The database is upgraded automatically before the application starts serving. This can take a while
after a large upgrade; the health check allows for it.

## Checking the version

**Console → System → Build**, and the footer of every page, show the version, commit and build date.

## Going back

Running an older image against an upgraded database is not supported. To go back, restore the backup
taken in step 2 with the older version; see [Backup and restore](backup-and-restore.md#restoring).

## Building from source

To build the images from a checkout instead of pulling them, add `docker-compose.build.yml`:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

A modified build must point `CINOMNI_SOURCE_URL` at its own source code, as the licence (AGPL-3.0)
requires; see [DEPLOYMENT.md §7](../../DEPLOYMENT.md#7-upgrading).
