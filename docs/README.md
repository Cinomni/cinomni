# Cinomni documentation

Guides for the people who install, run and use Cinomni. They describe what the current release
actually does; planned work lives in [ROADMAP.md](../ROADMAP.md), not here.

> Cinomni is **alpha** software. Read the release notes in [CHANGELOG.md](../CHANGELOG.md) before
> you run it, and keep backups.

## Getting started

| Guide | Read it when |
|---|---|
| [What Cinomni is](getting-started/overview.md) | You want to know what it does and how the pieces fit |
| [Requirements](getting-started/requirements.md) | You are choosing a machine and disks |
| [Installation](getting-started/installation.md) | You are installing it with Docker |
| [First run](getting-started/first-run.md) | It is running and you are creating the administrator |

## Using Cinomni

For everyone in the household.

| Guide | Covers |
|---|---|
| [Browsing the library](user-guide/library.md) | Home, Movies, Series, Upcoming, search |
| [Adding movies and series](user-guide/adding-titles.md) | Adding a title, monitoring, artwork, removing |
| [Requests](user-guide/requests.md) | Asking for a title and following it |
| [Watching](user-guide/playback.md) | The player, keyboard shortcuts, why a video is converted |
| [Subtitles](user-guide/subtitles.md) | Where subtitles come from and how to pick them |
| [Your account](user-guide/account.md) | Two-factor sign-in and recovery codes |

## Administration

For the person who runs the installation.

| Guide | Covers |
|---|---|
| [The console](administration/console.md) | A map of **Administration** and what each page is for |
| [Indexers](administration/indexers.md) | Where releases are searched for |
| [Profiles and decisions](administration/profiles-and-decisions.md) | Quality, cutoff, upgrades, interactive search |
| [Downloads](administration/downloads.md) | The torrent client, the Activity page, egress and the VPN |
| [Import and storage](administration/import-and-storage.md) | Hardlinks, naming, the recycle folder, path repair |
| [Users and access](administration/users-and-access.md) | Roles, request permissions, collections, content ceiling |
| [Notifications](administration/notifications.md) | Discord and webhook channels |
| [Settings reference](administration/settings.md) | Every page under **Console → Settings** |
| [Transcoding and hardware](administration/transcoding.md) | Direct Play, remux, transcode, GPUs |
| [Networking and reverse proxies](administration/networking.md) | Reaching Cinomni from outside the machine |
| [Backup and restore](administration/backup-and-restore.md) | What is backed up, and how to restore |
| [Upgrading](administration/upgrading.md) | Moving to a newer build |
| [Troubleshooting](administration/troubleshooting.md) | Health checks, logs and common problems |

## Reference and help

| Page | |
|---|---|
| [FAQ](faq.md) | Short answers to common questions |
| [Glossary](glossary.md) | The words Cinomni uses |
| [Getting help](getting-help.md) | Reporting a bug or a vulnerability |

## Deeper references

These guides summarise; the documents at the repository root are the complete references and win
when the two disagree.

- [DEPLOYMENT.md](../DEPLOYMENT.md): every setting, the storage contract, the VPN overlay, the
  hardware test, backups and the full troubleshooting table.
- [SECURITY.md](../SECURITY.md): the security model and what an internet-facing deployment needs.
- [CONTRIBUTING.md](../CONTRIBUTING.md): building, testing and the module architecture.
