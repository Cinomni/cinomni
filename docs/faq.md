# Frequently asked questions

### Does Cinomni come with any content or sources?

No. It does not host, provide or index content. You configure your own indexers, and you are
responsible for using it only with content you have the right to download and store.

### Can I use it without downloading anything, just to play my existing files?

Cinomni is built around acquisition: a title enters the library by being added, and a file becomes
playable by being imported. Scanning an existing folder of media into the library is not a feature of
the current version.

### Do I need a VPN?

No, but choose deliberately. With `CINOMNI_TORRENT_EGRESS=direct`, torrent traffic uses your own
connection and your public address is visible to the swarm. With the VPN overlay, the torrent engine
can only reach the internet through the tunnel and stops when it drops. See
[Downloads](administration/downloads.md#egress-direct-or-through-a-vpn).

### Does it support Usenet?

No. Newznab indexers can be added, but only torrent releases can be downloaded.

### Are there apps for phones or TVs?

No native apps. The web interface works in the browser of a phone, tablet, computer or smart TV.
Clients built for other media servers cannot connect to Cinomni.

### Does it run on a Raspberry Pi or an ARM NAS?

No. Only `linux/amd64` images are published.

### Can I run it on Windows or macOS?

Through Docker Desktop, for trying it out, without GPU transcoding. It is not a supported deployment.

### Why did my download get copied instead of hardlinked?

The downloads and library folders are on different filesystems, or the two containers run as different
users. See [Import and storage](administration/import-and-storage.md#hardlink-or-copy).

### Why was this release chosen (or not)?

Open the title and look at its evaluations, or run **Find releases**: every release carries the
reasons it was accepted or rejected. See [Profiles and decisions](administration/profiles-and-decisions.md).

### Where did the old file go after an upgrade?

To `library/.recycle`. Nothing is deleted automatically; empty it yourself when you are sure.

### Why is my video being transcoded?

Select the method badge (*Transcode*) at the top of the player: it lists the reasons. See
[Watching](user-guide/playback.md#direct-play-remux-and-transcode).

### How do I change my password?

Not possible from the interface in this version; it is planned before the beta.

### Can a child's account be restricted?

Yes: with a content ceiling (an age rating limit) and with restricted collections. See
[Users and access](administration/users-and-access.md).

### Is my data sent anywhere?

Cinomni contacts only what you configure: metadata and subtitle providers, your indexers, notification
channels, and the torrent swarm. Telemetry is off unless you point `CINOMNI_OTLP_ENDPOINT` at your
own collector, and even then paths, titles, addresses and keys are never exported.

### How do I know which version I am running?

**Console → System → Build**, or the footer of any page.
