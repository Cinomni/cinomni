# Import and storage

When a download finishes, Cinomni imports it: it finds the video files, matches them to the title or
the episodes, probes their streams with ffprobe, and places them in the library.

## Hardlink or copy

Cinomni **hardlinks** a finished download into the library: the same bytes appear in both folders,
using the space once, and the torrent can keep seeding. This only works when:

- the downloads and library folders are on **the same filesystem**, and
- both containers run as **the same user** (`CINOMNI_UID` / `CINOMNI_GID`).

When either is not true, the import **copies** the file instead: it still works, but it takes twice
the space until the download is removed. Cinomni checks this at every start and warns in the log; each
import job also records, file by file, whether it hardlinked or copied (**Console → Imports**, open a
job, *File operations*).

The packaged `docker-compose.yml` mounts one host directory at `/data` in both containers, which
satisfies both rules. Keep it that way if you change the layout, and keep the path identical in both
containers. [DEPLOYMENT.md §4](../../DEPLOYMENT.md#4-the-storage-contract) has the full contract.

## Library layout and naming

Imported files live under `library/`, one folder per title. Series are named from the catalog title.
For movies, **Console → Settings → Import → Movie file names** chooses:

| Value | Result |
|---|---|
| `ReleaseName` | Keep the downloaded file name. |
| `TitleYear` | Name the folder and file from the catalog title and year. |

A change applies to new imports only. File names are cleaned of characters that are unsafe on disk.

## Season packs

A download that contains a whole season is one import job that matches every episode file inside it.
Open the job under **Console → Imports** to see every file it matched.

## The recycle folder

When an upgrade replaces a file, the old one is moved to `library/.recycle`, never deleted. **Nothing
empties that folder**: deleting the only copy of something is left to you. Review it from time to
time, and include it in your backups.

## Symbolic links

A symbolic link inside the library or downloads folder must point inside that same folder. Cinomni
refuses to import through a link that leads elsewhere, because a torrent can contain links. To keep
the library on another disk, point the **root** (`CINOMNI_DATA_DIR`) at that disk rather than linking
a subfolder to it.

## Library path repair

Earlier builds could write some file names without cleaning them. **Console → Imports → Library path
repair** lists the stored files whose name would change. The list is a preview computed without
touching the disk; confirm to rename them.

## When an import fails

The job's page shows its attempts and history with the reason. Interrupted imports (a restart in the
middle) resume on the next start. See also [Troubleshooting](troubleshooting.md).
