# The console

**Administration** in the side bar opens the console: the pages that configure and diagnose the
installation. Only administrators can open it; the server refuses every console request from a member
account whatever the interface shows.

## System

| Page | What it is for |
|---|---|
| **System** | *Readiness* of the database, the torrent engine and the storage folders; *Torrent egress* (direct, or through a verified VPN tunnel); the *Build* you are running. Start here when something looks wrong. |
| **Operations** | The machinery behind automation: the *Backlog* of queued work, *Failed commands* (with **Retry**), *Scheduled jobs* and when they last ran, and the *Retention* policy that purges old records. |
| **Settings** | Every setting that can be changed while Cinomni runs. See the [settings reference](settings.md). |

## Library

| Page | What it is for |
|---|---|
| **Collections** | Groups of titles, open to everyone or restricted to the accounts you grant. See [Users and access](users-and-access.md). |
| **Wanted** | Monitored titles and episodes that are not in the library yet, with a **Search** button. An episode that has not aired is listed as unaired, not missing. |
| **Imports** | Every import job: which files it matched, what it did to each (hardlink, copy, rename), and its history. Also *Library path repair*. See [Import and storage](import-and-storage.md). |
| **Trending** | An optional list that adds TMDB trending titles to the catalog, unmonitored. Off until you enable **Add trending titles** in Settings → Catalog. |

## Acquisition

| Page | What it is for |
|---|---|
| **Indexers** | Where releases are searched for. See [Indexers](indexers.md). |
| **Profiles** | Quality cutoff and upgrades per profile, and the list of blocked releases. See [Profiles and decisions](profiles-and-decisions.md). |

## Notifications and people

| Page | What it is for |
|---|---|
| **Channels** | Discord and webhook destinations for notifications. See [Notifications](notifications.md). |
| **Users** | Accounts, roles and permissions. See [Users and access](users-and-access.md). |

## Activity

**Activity** sits next to **Administration** in the side bar. It shows each title Cinomni is trying to
acquire (its *intent*), every attempt, the download with its progress, and the state history, so
you can see what is happening and why. Downloads can be paused, resumed and retried from there. See
[Downloads](downloads.md).

## Live updates

Pages update on their own as things change on the server (downloads progress, imports finish, new
requests arrive). There is no need to reload.
