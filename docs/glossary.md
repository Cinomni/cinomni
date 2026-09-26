# Glossary

**Acquisition (intent).** Cinomni's record that it is trying to get a specific title or episode. It
survives failures and restarts: a failed download is an attempt, not the end of the intent.

**Acquisition profile.** The rules a release is judged by: allowed qualities with their ranks, formats
that add to the score, size limits, the cutoff and whether upgrades are allowed.

**Collection.** A shelf of titles that decides who can see them: open to everyone, or only to the
accounts granted access. Every title is in exactly one collection.

**Collection rule.** Conditions on a title's facts (kind, genre, rating, year, runtime, language,
title) that place matching titles in a collection.

**Content ceiling.** The highest age rating an account may see.

**Cutoff.** The quality rank at which a title is good enough and is no longer searched for.

**Direct Play.** Sending a file to the browser unchanged.

**Egress.** How torrent traffic leaves the server: `direct` over your own connection, or through a VPN
tunnel.

**Evaluation.** The verdict of a profile on one release, with its reasons and score.

**Evaluation order.** The order in which collections' rules are tried. The first collection whose rule
matches a title claims it.

**Hardlink.** A second name for the same file on disk. It lets a download appear in the library without
using space twice. Only possible within one filesystem.

**Held download.** A download paused by Cinomni because the VPN tunnel is not verified. It resumes
by itself.

**HLS.** The streaming format Cinomni converts to when a browser cannot play a file directly.

**Indexer.** A service Cinomni searches for releases: Torznab, Newznab, a built-in site, or a site
described by a definition.

**Monitoring.** Whether Cinomni searches for a title (or episode) on its own.

**Pinned.** A title moved to a collection by hand. Rules leave it where it is until the pin is
released.

**Recycle folder.** `library/.recycle`, where replaced files go. Never emptied automatically.

**Release.** One specific upload of a title (a torrent), with a name that describes its quality,
source and format.

**Remux.** Repackaging a video into another container without re-encoding it.

**Sidecar.** The separate container that runs the BitTorrent engine.

**Tone mapping.** Converting an HDR picture to SDR so it looks right on a screen without HDR.

**Transcode.** Re-encoding a video on the fly, on the CPU or a GPU.
