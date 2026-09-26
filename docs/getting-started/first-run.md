# First run

## 1. Create the administrator

Open `http://127.0.0.1:8080` on the server itself (or through an SSH tunnel). On a fresh installation
the page asks you to **create the administrator account**: pick a username and a password of at least
8 characters, then **Create account**.

Do this before Cinomni is reachable from any other machine. The setup page is open to whoever reaches
it first, and the first account becomes the administrator.

## 2. Check that it is healthy

Open **Administration** (the server icon in the side bar). The **System** page shows:

- **Readiness**: the database, the torrent engine and the storage folders. Everything should be
  healthy. A *degraded* torrent engine or storage is explained in
  [Troubleshooting](../administration/troubleshooting.md).
- **Torrent egress**: whether torrent traffic goes directly over your connection or through a
  verified VPN tunnel.
- **Build**: the version you are running.

## 3. Connect your sources

Work through this list in order. Each step switches on part of the pipeline; none of them is needed
to start the application.

1. **Metadata.** Under **Console → Settings → Metadata**, enter a TMDB API key (movies need it) and a
   TheTVDB key (series need it for their seasons and episodes), unless you set them in `.env`. Keys
   entered here are stored encrypted, so `CINOMNI_SECRET_KEY` must be set.
2. **Indexers.** Under **Console → Indexers**, add at least one place to search for releases. See
   [Indexers](../administration/indexers.md).
3. **Profiles.** Under **Console → Profiles**, check the acquisition profiles: which qualities are
   acceptable and at which one Cinomni stops upgrading. See
   [Profiles and decisions](../administration/profiles-and-decisions.md).
4. **Subtitles** (optional). Set the wanted languages under **Console → Settings → Subtitles**, with an
   OpenSubtitles or SubDL key configured. See [Subtitles](../user-guide/subtitles.md).
5. **Notifications** (optional). Under **Console → Channels**, add a Discord or webhook channel. See
   [Notifications](../administration/notifications.md).

## 4. Add your first title

Use **Add** to find a movie or a series and add it with monitoring on. Cinomni searches your
indexers, picks a release, downloads it, imports it and makes it playable. You can follow each step on
**Activity** and on the title's own page. See [Adding movies and series](../user-guide/adding-titles.md).

## 5. Invite the household

Under **Console → Users**, create an account for each person, as a *Member* unless they should run the
installation. Decide whether each one can request titles and whether their requests need your
approval. See [Users and access](../administration/users-and-access.md).

## 6. Before you rely on it

- Protect your own account with [two-factor sign-in](../user-guide/account.md), and store the
  recovery codes somewhere safe.
- Read [Backup and restore](../administration/backup-and-restore.md). Cinomni backs up its database
  every day; your media files are yours to back up.
- If people will reach Cinomni from outside the server, read
  [Networking and reverse proxies](../administration/networking.md) first.
