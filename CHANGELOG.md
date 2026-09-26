# Changelog

What changed between releases, for somebody who runs Cinomni rather than develops it. It records the
things that change a decision (what is new, what behaves differently, what an upgrade needs you to
do), and not every commit. [ROADMAP.md](./ROADMAP.md) is the other half of the picture: it says where
the project is going and what it deliberately does not claim.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning: [SemVer](https://semver.org/),
under the rules in *Versioning* below.

## Unreleased

### Added

- **Rule-based collections.** In Console → Collections, a collection can carry rules on
  kind, genre, age rating, year, runtime, original language or title, and the collections are asked in
  an order you set: the first rule that matches decides which shelf a title sits on, and therefore who
  can see it. Every rule or order change can be previewed first, and the preview lists what would
  move, including titles that would fall back to the open default collection. Titles are placed when
  they are added and again when their metadata arrives. Saving is only possible after a preview, and
  it warns when titles would leave a restricted collection for an open one.
- **New titles wait for their metadata while rules exist.** A title added while any collection rule
  exists is hidden from members until its metadata arrives, because until then the rules cannot see
  its genres or age rating and it would otherwise sit on the open default collection. Administrators
  see it with a notice; placing it by hand (through the API for now) also releases it. Titles added without a provider id are
  not held, since nothing would ever release them.

### Changed

- **Moving a title to a collection by hand now pins it there.** Rules leave a pinned title alone until
  the pin is released.

### Upgrading

- Titles that already sit outside the default collection are pinned by the upgrade, so saving the
  first rule cannot move them and nobody gains or loses sight of anything. Existing collections are
  asked in the order they were created.

## [0.1.0-alpha.1] - 2026-09-26

**The first public version, and an alpha.** Everything the project has built so far, released for
people who run it knowingly: the movie and series flow works end to end and survives restarts, but
capabilities are still landing, there is no upgrade history yet, and the known gaps below are real.
Read *Versioning* for what an alpha does and does not promise.

**Known gaps in this alpha:**

- An account cannot change its password; recovering one needs the SQL in DEPLOYMENT.md.
- Transcoding a 4K HDR source on the CPU needs more than the default `CINOMNI_CPUS=4.0` /
  `CINOMNI_MEMORY=2g`; with the defaults, FFmpeg runs out of memory and playback stops.
- No real GPU and no real VPN tunnel drop have been tested (see *Accepted risks* in ROADMAP.md).
- Dolby Vision profile 5 tone-maps with wrong colours.

### Added

- **A player with real choices.** The web player has its own controls (seek bar showing what is
  buffered and how far a conversion has got, ±10 s, volume, speed, full screen, picture in picture)
  and keyboard shortcuts. Viewers pick the audio track, the subtitles and a quality (1080p/720p/480p/
  360p with a bitrate cap); audio and quality open a new stream from where they are, subtitles switch
  in place. The tracks chosen are remembered per title.
- **Subtitles in the browser.** Text subtitles inside the file and the ones Cinomni fetched (SubRip,
  ASS, WebVTT) are served as WebVTT and drawn by the player; an unmarked SubRip in Windows-1252 shows
  its accents. Picture subtitles (PGS, VobSub) can be burned into the video, which always converts.
- **Transcoding settings in the console.** Console → Settings → Playback now holds the preferred
  hardware backend, hardware decoding, the output codec (H.264, or HEVC when the device plays it and
  the GPU encodes it), encoder speed and quality, a server-wide resolution and bitrate ceiling,
  software threads, HDR tone mapping and its curve, audio channels and bitrate, and picture-subtitle
  burn-in. Every key can also be pinned by environment variable (`Playback__Transcoding__*`).
- **Hardware that is tested, not assumed.** Each backend is tried with a real encode and decode at
  startup (VAAPI, Quick Sync and NVENC on Linux, plus AMF on a host running natively on Windows), and
  only what passes is used. The console shows every test and its failure, and **Run hardware test**
  repeats it without a restart.
- **HDR and Dolby Vision to SDR.** A transcoded HDR10, HLG or Dolby Vision source is tone-mapped so it
  no longer looks washed out; decode stays on the GPU where there is one. Dolby Vision profile 5 may
  still show wrong colours.
- **A conversion starts where you are.** Resuming or seeking far into a transcoded film starts the
  conversion there instead of waiting for it to catch up. The full timeline comes from the runtime
  and bitrate Import now records for each file (migration `AddVersionRuntime`, applied at startup);
  a file imported earlier gets its runtime from the first time it is played.

- **A content ceiling, per account and off by default.** An administrator can hide titles above a
  classification for one account. Unrated titles stay visible. Only US, ES, DE and GB have a ladder;
  any other region cannot have a ceiling set. A ceiling does not survive a region change: it stops
  applying, and the account page says so, rather than enforcing the old certificate under the new
  rules. Administrators are not filtered.

- **Declarative indexer login.** A definition that declares a `session.login` block now signs in with
  the indexer's stored credential, keeps the session, and notices when the site expires it. The
  indexer listing reports whether a login is declared, whether a session is held, and when it was
  captured; testing an indexer reports whether the test searched authenticated.
- **A build identity.** `GET /api/system/info` reports the version, the commit and the build date, and
  the web client shows them. A build that was not stamped says so rather than guessing (see
  *Which build am I running?* in [DEPLOYMENT.md](./DEPLOYMENT.md)).
- **Indexer catalog sources.** Cinomni ships no list of sites. The catalog in the console is empty
  until you add a catalog source: a name and an https URL you choose, publishing a JSON manifest of
  indexer definitions in Cinomni's own format ([docs/indexer-catalog-format.md](./docs/indexer-catalog-format.md)).
  Cinomni fetches it when you add it and whenever you press *Refresh*, keeps the last valid manifest so
  the catalog survives a restart or the source being down, and shows why a refresh failed. A manifest
  with any invalid entry (including one that declares a login) is rejected whole and the previous one
  kept. Two sources may offer the same key; each installs its own indexer.
- **Published images and releases.** Every image is published to GitHub Container Registry
  (`ghcr.io/cinomni/cinomni`, its `-hwaccel` variant, and the three sidecar images), stamped with the
  version, the commit and the build date, and `docker-compose.yml` pulls them. `CINOMNI_IMAGE_TAG` in
  `.env` chooses the version; `docker-compose.build.yml` builds from source instead.
- **Per-indexer credentials.** Encrypted at rest under `CINOMNI_SECRET_KEY`, write-only over HTTP.
  Torznab indexers finally send their API key, which they never did before.
- **Hardware transcoding** on VAAPI, NVENC and QSV, behind two opt-in Compose overlays. Never run
  against a real GPU; see *Accepted risks* in [ROADMAP.md](./ROADMAP.md).
- **An operator console** at `/console`, and a settings store that changes eleven keys without a
  restart.
- **A throttle on the anonymous credential routes.** Login and first-run setup are rate-limited per
  client address, answering 429 with a `Retry-After`; the permits and the window are settings an
  operator changes without a restart. IPv6 clients are counted by /64 prefix, and a shared
  concurrency backstop bounds simultaneous password verifications however many addresses appear.
  Publishing beyond loopback needs `CINOMNI_TRUSTED_PROXIES` **and** a proxy that writes
  `X-Forwarded-For` itself (see [DEPLOYMENT.md](./DEPLOYMENT.md)).

- **Two-factor authentication (TOTP), per account and opt-in.** Enroll from your own account with a
  QR code or the secret, confirm with a code, and keep the ten single-use recovery codes it shows
  once. Signing in becomes two steps: the password returns a short-lived challenge, and a code
  answers it. Turning it off needs the password and a current code, and ends every other session.
  Recovery codes work even if `CINOMNI_SECRET_KEY` is lost, and DEPLOYMENT.md documents the way back
  when those are gone too.
- **A content security policy, and the hardening headers around it**, on every response the Host
  serves. `script-src 'self'` with no `unsafe-inline` or `unsafe-eval` is what protects the session
  token the client keeps in `localStorage`; `Referrer-Policy: no-referrer` stops the metadata
  provider's image host from being told which page a viewer is on. HSTS is sent only over TLS, which
  for this image means only behind a proxy that forwards the scheme.

- **A cap on how many requests one account may have open at once.** Off by default, so nothing
  changes until an administrator sets it; a per-account limit overrides the installation default in
  either direction, and zero lifts the cap. Open means pending or approved-and-not-yet-playable; a
  rejected request and a fulfilled one both give the slot back, so the limit is on how much of the
  approval queue one person occupies rather than on how much they may ask for over a lifetime. The
  refusal names the numbers, so the interface can say "you have 5 of 5 open" rather than "no".
  <br>There is deliberately **no download quota**: an acquisition carries no account, because after
  approval the spine runs on monitoring policy rather than on people, and one season pack can satisfy
  what three people asked for. Bounding disk or bandwidth is an installation-level concern and a
  different feature.

### Fixed

- **A transcode no longer tries NVENC on a machine without an NVIDIA GPU.** Detection took FFmpeg's
  encoder list at its word, so every conversion first failed on NVENC and only then fell back to the
  CPU. The NVIDIA overlay also asked for too little of the driver (`compute` is now included); without
  it NVENC could not start at all.
- **A file converted for a smaller screen is now actually scaled down**, and a file with an extra
  audio track the device cannot play (a DTS track beside an AAC one) is no longer converted entirely.

- **The two-step sign-in no longer runs out early on a fast clock.** The code screen counted down
  to the server's expiry time using the browser's clock; a device running ahead of the server sent
  its user back to the password before the request had expired. It now counts the lifetime the
  server gives from when the reply arrived.
- **Disabling an account, or giving up your own administrator role, asks first.** Demoting yourself
  also reloads who you are, so the console closes rather than refusing every click.
- **A setting someone else changed meanwhile reloads** instead of failing on every retry.
- **Failures that used to be silent now say so**: unblocking a release, switching a notification
  channel on or off, granting or revoking access to a collection, and reading who has that access.
- **A title you just added stops offering "Add"** and shows in the library without a reload.
- **Artwork choices have names** a screen reader can announce.
- **Titles from the trending list arrive unwatched.** Turning the list on used to start looking for
  every title on it, up to forty every six hours. They are now catalogued with monitoring off: switch
  on the ones you want from the title's page. When a household member's request for one of them is
  approved, the approval switches it on. A title an approval finds already switched off (by the list,
  or by you) is switched on too. A narrower policy you set is kept. **Titles the list added before
  this change stay monitored**: the console's *Trending* page lists them, if you want to switch them
  off.
- **The trending list no longer stops at the first title that fails**, and a title whose details could
  not be fetched is tried again on the next run rather than left bare.
- **The calendar lists episodes only**, as it always did in practice: Cinomni has no release date for
  a movie to put on it, and the page no longer says it shows movies. A window that runs past the year
  9999 is refused with 400 instead of failing with 500.
- **A release that was blocked when a search found it is recorded as rejected**, so it is cleaned up
  with the other rejections instead of being kept forever as if it had been accepted. Unblocking it
  now says so on its explanation, and grabbing it afterwards neither asks twice nor is recorded as
  overriding the profile. The same goes for a release passed over because it already failed that
  title: it is recorded as rejected, and grabbing it by hand is an override. Blocking and grabbing
  the same release at the same moment no longer fails with 500.
- **A SubDL subtitle is stored in the format it is.** Its format was guessed from pieces of its name,
  so `The.Assassin.srt` was saved as `.ass` and a VobSub `.sub` as `.srt`; it now comes from the
  declared format or extension and is confirmed against the file's own first lines. A `.sub` is
  skipped, since it cannot be served.
- **A metadata provider that answers with malformed JSON backs off** and is reported like any other
  failure, instead of being retried at once.
- **A TheTVDB token the provider stops accepting is replaced at once** (the request signs in again
  and is sent once more) rather than failing every call until its assumed 25-day lifetime ran out.
- **A FlareSolverr challenge gets its full minute.** Cinomni waited 30 seconds for a solve it had also
  allowed 30 seconds, so a slow challenge always failed. A browser request that runs out of time now
  fails that request only, instead of ending the whole search, and an indexer whose detail pages fail
  twice in a row is not asked for the rest.
- **Indexer settings that do not fit are a named 400**, not a 500: a name, base URL, credential
  username or definition name over its length, capability lists over 200 characters or a search
  parameter containing a comma. Setting capabilities answers 400 for such a list; only an unknown
  indexer is a 404.
- **A definition with a CSS selector that cannot compile is refused** when it is uploaded, dry-run or
  installed from the catalog, naming the rule. A definition already stored keeps working as it did.
  `search.pagination` is still accepted but was never followed; a search reads the first page only.
- A negative size or seeder count from an indexer is read as zero, so a minimum-seeders setting still
  refuses it.

- **A download that stops moving fails after a day** and its goal searches for another release; it used
  to sit in Downloading for ever. Paused, network-held and engine-queued downloads are waiting, not
  stalled. The failed torrent leaves the engine, as one the engine failed does. Needs the
  `TrackTransferProgress` migration, applied at startup; `Downloads__Transfers__StallTimeout` changes
  the day.
- **New downloads stop seeding at a ratio of 1.0 or after 7 days**, whichever comes first; they seeded
  without limit. `Downloads__Transfers__SeedRatioLimit` and `SeedTimeLimit` change either bound, and
  `none` lifts it. A download already seeding keeps the rule it had.
- **A finished download the sidecar no longer holds is closed** (`SeedingLost`) instead of showing
  Seeding for good; a sidecar restart ends seeding.
- **Pausing or resuming a finished download is a 409** that changes nothing. It used to pause the
  seeding torrent and then answer 500.
- **Resuming a held download under `pause-and-alert` actually resumes it.** The sidecar's own hold kept
  the torrent still.
- **A release that failed can be tried again** without restarting the sidecar, and the retry writes to
  its own folder.
- **The sidecar rejoins a tunnel that restarted.** It exits once the tunnel device has been missing for
  two minutes so its restart policy starts it in the tunnel's new namespace, and Compose restarts it
  with a tunnel it recreates.
- qBittorrent's queue (`queuedDL`) and its 5.x `stoppedDL`/`stoppedUP` states are read correctly.

- **A body the API cannot use is a 400 in the error envelope**, not a bare 500: a login missing its
  password, JSON that does not parse, a value of the wrong type. An exception no endpoint caught is a
  500 in the envelope too, with fixed text and nothing of the exception.
- **Two administrators can no longer be demoted or disabled at the same moment into none**, and two
  first-run setups sent at once no longer create two administrators.
- **A username longer than 100 characters is refused by name** instead of failing the insert.
- **A role that does not exist (`"role": 7`) and an open-request limit below 0 or above 1,000 are
  refused.** A negative limit used to be stored and read as "no limit"; the `RememberAcceptedTotpStep`
  migration rewrites any stored one to 0, which means the same thing.
- **The web client no longer signs you out when the server is down.** Reloading a tab while the server
  restarted, or behind a proxy that answered with its own error page, threw the session away. Only a
  refused token ends it now; anything else shows "Cinomni can't be reached" with a retry, still
  signed in.
- **Tabs follow a sign-in or sign-out made in another tab.** A tab kept showing the previous account
  and its cached data while its requests carried the new account's token, and a request still in
  flight on the old token could sign the new session straight back out.
- **Changing one permission no longer touches a content ceiling set under another region.** The users
  page sent the ceiling back with every toggle; after a region change that either refused the toggle
  or quietly switched the old restriction back on. An unchanged ceiling is now kept exactly as it was.
- **Imported titles show up without a reload** on the series page, the player and the calendar: the
  live updates were invalidating cache keys no page used.
- **A transcoded stream that dies shows the retry screen** instead of a black player, and Safari (which
  plays HLS itself) can play transcoded sessions at all: its segment requests carried no token and
  were refused. The playlist served to it now hands the same token on to each segment, and is sent
  with `Cache-Control: no-store`.
- **An event whose handler keeps failing no longer stops every event behind it.** Each one is delivered
  on its own; a failure is retried with a growing backoff (30 seconds, doubling, at most an hour) and
  after 10 attempts the event is set aside as a dead letter: kept, unpublished, for 180 days, counted
  by `cinomni.outbox.dead_lettered` and logged with its id. There is no screen for them yet: list them
  with `SELECT id, event_type, attempts, last_error FROM operations.outbox WHERE dead_lettered_at IS NOT
  NULL`, and retry one by clearing its `dead_lettered_at` and `next_attempt_at`. Events are no longer
  guaranteed to arrive in order when one of them fails. Needs the `DeadLetterOutboxMessages` migration,
  applied at startup.
- **Each download has a folder of its own** (`<staging>/<attempt id>/`). Two releases that name their
  folder alike used to share one, and what a failed one left behind could be imported as the other.
  Downloads already under way keep their folder.
- **An indexer that answers with an error is a failed indexer**, not one with nothing to offer: a Torznab
  `<error>` sent with status 200 (wrong API key, request limit) and an unreadable feed both fail the
  search and the indexer test.
- **Indexer links resolve the same on Linux as elsewhere.** A link such as `/download/1.torrent` was read
  as a file on the local disk and refused.
- **One download that cannot be checkpointed no longer costs every other download its checkpoint**, and a
  large torrent's checkpoint fits (the control channel carries up to 32 MiB either way).
- **Torrents that are v2 only each keep their own identity**; they all shared one before.
- **Import refuses a download whose folder is the staging area itself**, or lies outside it, instead of
  scanning every other download.
- **Subtitles already in a file count as present**, so the catch-up moves on to the files that need one.
- **The calendar reads every title at once**, and a member's page is filled with what they may see
  before it is cut at 500.

- **Transcoding has a lifecycle.** FFmpeg used to be started and forgotten: nothing read its error
  output, so a long conversion could stall once the pipe filled; stopping playback never stopped it;
  any member could start as many as they liked; and each converted copy stayed on disk for the 90 days
  the session row is kept. Now stopping playback stops FFmpeg and removes its output, a stream nothing
  has asked for in five minutes is closed the same way, at most `CINOMNI_MAX_TRANSCODES` streams (4)
  are converted at once and at most `CINOMNI_MAX_TRANSCODES_PER_ACCOUNT` (2) per account, and how
  FFmpeg ended (finished, or failed with its last words) is recorded on the transcode job. A restart
  picks a finished conversion back up and removes a half-done one. A refused request answers `429`
  with `playback.transcode_limit` and a message saying which limit was hit; **a fifth concurrent
  conversion that used to start is now refused**, so raise the limit if your hardware can take it.
  A conversion that fails part way now ends its session instead of leaving the player buffering.
  A stream ends at `Playback:MaxTranscodeLifetime` (6 hours) however busy its player keeps it, and
  as soon as its viewer loses access to the title. A crashed host's leftover FFmpeg is stopped on
  the next start. Every session records why it ended (`endReason` on the session detail), and the
  `429` carries `Retry-After`. `cinomni.transcode.active` no longer drops at the watched threshold
  while the credits are still being converted.
  **Startup now refuses a `Playback:TranscodeRoot` that overlaps the library or the staging area**,
  because Cinomni deletes in that directory on its own.
- **An import could destroy the copy it had just set aside.** A relaunched upgrade found its own
  earlier landing at the library path, took it for an older copy and moved it into the recycle bin,
  on top of the previous copy the bin was keeping. It now recognises its own landing by its whole
  content, and nothing in Import ever deletes or overwrites a file it did not create: the bin numbers a
  second copy instead, and a failed landing only rolls back what it wrote itself.
- **An upgrade that landed on the same path never registered.** Library's unique path counted the
  version being replaced, so the registration failed on every retry and the library went on offering
  a file that was in the bin. Replaced versions are now retired (migration `RetireReplacedVersions`
  retires those of assets already upgraded away) and only live versions must be unique, including for
  assets registered before unit links existed. An asset that was upgraded away no longer plays: its
  streams describe a file that is no longer there.
- **Two titles with the same name and year shared one file.** The second import recycled the first
  title's film as if it were an older copy of itself, so a viewer allowed to see one could end up
  playing the other. Each title now owns its folder: a folder another title already has files in
  (as Import itself recorded them, not only once Library has heard) is never shared, and the new title
  lands in `Title (Year) [xxxxxxxx]`, tagged with the end of its work's id.
- A movie whose release name was longer than a path segment lost its container extension; it keeps it
  now. The path-repair pass moved any file that merely began with a video's name (a second film, an
  extended cut) as its sidecar, and overwrote a sidecar whose new name was taken; it now moves only
  `<name>.…` files that are not videos, and reports the repair blocked, moving nothing, when a sidecar
  could not follow.
- **The opt-in qBittorrent engine could not start the host at all**: the staging directory was only
  registered with the sidecar. With it running, a magnet from an indexer's feed was handed to
  qBittorrent as it arrived, and qBittorrent splits `urls` on line breaks, so a feed could slip a
  second torrent or an internal URL in; the link is now rebuilt from its info-hash, name and valid
  trackers. A `.torrent` it fetched had no size ceiling (now 8 MiB, like the sidecar's); a torrent the
  household had added to the client itself was taken over (it is now adopted only if it saves into
  Cinomni's staging area); and a hostile `.torrent` could throw from the info-hash reader.
- A film restarted from its last saved position whenever the live-update connection dropped and came
  back: the reconnect re-read every cached query, including the one that opens the playback session.
- A playback request that failed answered with the message in `error` and no `message`, so the player
  showed the bare HTTP status text. It now uses the `{ error, message }` envelope like every other route.

- **Anime releases numbered in absolute terms now match on series no provider numbered.** Only
  TheTVDB publishes an absolute ordering, so a series catalogued from TMDB or TVMaze had none at all,
  and a release named `Show - 053` matched nothing, silently: the search found it, the evaluation
  could not identify it, and the title was simply never acquired. When no provider publishes one,
  Cinomni now counts the ordering from the season structure it already has, excluding specials. A
  counted number is recorded as counted, and gives way to a published one whenever a snapshot brings
  it. It assumes scene numbering follows air order, which is right for most anime and is exactly what
  a scene-mapping service exists to correct where it is not.

- Declarative indexers were configured, reported healthy, and silently returned nothing: the search
  path did not pass the definition to the adapter that runs it.
- Library filenames written by an earlier Linux build kept characters the sanitiser should have
  removed; `POST /api/imports/path-repair` fixes them on request, never automatically.

### Security

- **The web client uses `react-router` 7**, which closes an open redirect in `react-router-dom` 6
  that could lead to cross-site scripting, and two advisories fixed only in version 7. `npm audit`
  reports no known vulnerability.
- **An import never follows a symbolic link out of its root.** A torrent can create links; the scan of
  a download skips them, and path confinement resolves them before deciding a path is inside. **If a
  folder inside your library is a link to another disk, landings into it are now refused**: link the
  library root itself instead (see *The storage contract* in [DEPLOYMENT.md](./DEPLOYMENT.md)).
- **The OpenSubtitles API key goes only to the OpenSubtitles API**, not also to the host of each
  download link.
- **`CINOMNI_SIDECAR_CONTROL_TOKEN` is required in every topology.** `docker compose up` refuses to
  start without it; before, only the VPN overlay did. **Set it in `.env` before upgrading**
  (`openssl rand -hex 32`).
- **The sidecar never connects to this machine or its networks.** Peers and trackers in private,
  loopback, link-local, CGNAT and multicast ranges are refused, and a resume checkpoint can neither
  turn that off nor bring remembered peers back. A tracker on your own network is unreachable.
- The test-torrent RPC answers only in the PoC compose file; it wrote 64 MiB per call.
- The application reaches the sidecar directly, never through `HTTP_PROXY`, so the control token
  cannot travel to a proxy.
- The development database listens on loopback only; its password is in the repository.

- **An authenticator code works once.** A code stays valid for up to ninety seconds; one seen over a
  shoulder or relayed by a phishing page could sign in again with it while it lasted, and the code
  that confirmed an enrollment could also complete the next sign-in. Needs the
  `RememberAcceptedTotpStep` migration, applied at startup.
- **Signing in to a disabled account takes as long as a wrong password.** It skipped the password
  check, and the faster answer said the account existed.
- The stored indexer session is bound to the credential that created it through the cipher itself,
  so a changed password cannot reuse the previous account's session, and no password material is
  stored in any column.
- A declarative login is refused over plain `http`, and a login that would carry the password in a
  query string is refused when the definition is uploaded.
- Passwords and usernames are length-bounded before they are hashed. A single permitted login
  could otherwise carry a password the size of the request-body limit and spend several times the
  memory the throttle was sized against.
- A failed sign-in now holds off the next attempt. Before, one wrong stored password meant one
  credential submission per scheduled search, indefinitely.
- **What an account may not see stays out of every answer, not only the library.** The notification
  inbox, "next up", the request list and the "already in your library" reply all consult the same
  content access as the library: a title hidden from you (restricted shelf or above your ceiling) is
  neither named nor linked. A request for such a title goes to an administrator, even from an account
  that approves its own; approving it does not grant access, and the request never shows as available.
  The metadata snapshot routes are now administrator-only.
- **The Spanish ceiling knows every label the provider uses**, and compares them without regard to case
  or spacing. `X` (adult film) and the television-only `13` and `10` used to be unranked, so visible under
  any ceiling; a ceiling of `12` now hides `13` and `X` as well.
- **A session token in a URL is accepted only to read a playback stream**: the one request a `<video>`
  element cannot send a header with. Everything else, the live-update stream included, needs the
  `Authorization` header. Stopping playback as the page closes no longer puts the token in the URL.
- **No outgoing request is logged with its address**: a Discord webhook's token and an indexer's API key
  travel in the URL.
- **Turning two-factor off answers a wrong password and a wrong code the same way**, and neither
  enrolling nor turning it off confirms a password any more.
- **The torrent sidecar refuses a `.torrent` larger than 8 MiB and caps how many calls it handles at
  once.** With qBittorrent, a staging area of `/` adopts no torrent at all.
- **The live-update stream follows the session.** It is checked every 15 seconds and closes on logout,
  a revoked session, a disabled account or changed permissions; the page reconnects as whoever the
  account is now. One account may hold at most 8 such streams, so it cannot take the 64 the installation
  allows from everyone else.

---

## Versioning

Cinomni is one deployable image with its web client inside it, so the version names **the
installation**, not a library API.

### The format

```text
MAJOR.MINOR.PATCH[-STAGE.N][+BUILD]

0.1.0-alpha.1                 an alpha
0.1.0-beta.2                  the second beta of the same version
0.1.0-rc.1                    a release candidate
0.1.0                         the version itself
0.1.0-alpha.1+1f7be88…        a build of 0.1.0-alpha.1 from that commit
```

- No `v` prefix. The same string is the git tag, the `CINOMNI_VERSION` build argument, the `<Version>`
  in `src/Directory.Build.props`, `version` in `web/package.json`, and what `GET /api/system/info`
  reports as `informationalVersion`.
- `STAGE` is one of `alpha`, `beta` or `rc`, always followed by a number that starts at 1.
- `+BUILD` is added by the build, never written by hand: it is the commit the image came from.

Versions sort as [SemVer](https://semver.org/#spec-item-11) says:
`0.1.0-alpha.1 < 0.1.0-alpha.2 < 0.1.0-beta.1 < 0.1.0-rc.1 < 0.1.0 < 0.1.1 < 0.2.0-alpha.1 < … < 1.0.0`.

### What each stage promises

| Stage | Example | New capabilities | Upgrading to the next version | Who it is for |
|---|---|---|---|---|
| **alpha** | `0.1.0-alpha.3` | Still landing, including large ones | Should work unattended; when it does not, the changelog says exactly what to do | People who run it knowingly and report problems |
| **beta** | `0.1.0-beta.1` | Frozen for this version: fixes only | Unattended, from the previous beta or the last alpha | Anyone willing to report problems |
| **rc** | `0.1.0-rc.1` | Frozen: only fixes for blockers | Unattended | Anyone; it becomes the release unless something blocks it |
| **release** | `0.1.0` | — | Unattended; the rules below apply | Everyone |

Every stage keeps two promises regardless: **no data loss that a backup does not recover**, and an
upgrade note in this file for anything an upgrade needs you to do.

### Before 1.0.0 and after

- **`0.x` versions carry no stability promise.** A `0.MINOR` bump may change something you relied on;
  when it does, the release notes open with an *Upgrade notes* section saying what and how.
- **`1.0.0` is the first stable version.** From it on, the rules below are binding.

From `1.0.0`:

- **MAJOR**: an upgrade needs you to do something, or something you relied on is gone. Concretely: a
  removed or renamed environment variable, a changed storage layout, a dropped Compose service, a
  removed HTTP route, a migration that is not safe to run on an existing installation.
- **MINOR**: new capability, upgrade unattended. New endpoints, new modules, new settings with
  defaults that preserve today's behaviour.
- **PATCH**: fixes and security work, no new capability.

### Moving between stages

| From | Next version | When |
|---|---|---|
| `X.Y.Z-alpha.N` | `X.Y.Z-alpha.(N+1)` | Another alpha: fixes and new capabilities |
| `X.Y.Z-alpha.N` | `X.Y.Z-beta.1` | The version's capabilities are complete and its beta criteria (ROADMAP.md, *Release plan*) are met |
| `X.Y.Z-beta.N` | `X.Y.Z-beta.(N+1)` | Fixes only |
| `X.Y.Z-beta.N` | `X.Y.Z-rc.1` | No known defect blocks the release |
| `X.Y.Z-rc.N` | `X.Y.Z` | A candidate has run without a blocker being found |
| `X.Y.Z` | `X.Y.(Z+1)` or `X.(Y+1).0-alpha.1` | A fix release, or the next version's first alpha |

A stage never goes backwards: a blocker found in a beta is fixed in the next beta, not by returning to
alpha.

Two things sit outside all of this and are worth stating so nobody looks for a guarantee that is not
there:

- **The HTTP API is internal.** The web client ships in the same image as the server that answers it,
  so the two are never mismatched, and no compatibility promise is made to a third-party client. What
  the version does promise about is the *deployment* contract: environment variables, the storage
  layout, the Compose topology.
- **Downgrades are not supported at any level.** Migrations run forward on startup and an older
  image does not know the shape the database has moved to. Restoring a backup taken before the
  upgrade is the way back, and the restore refuses a dump whose migration state does not match the
  binary. See *Backup and restore* in [DEPLOYMENT.md](./DEPLOYMENT.md).

### Cutting a release

1. Set the new version everywhere the source names it: `<Version>` in `src/Directory.Build.props`,
   `web/package.json` (`npm version <version> --no-git-tag-version` from `web/` keeps
   `package-lock.json` in step), and the `CINOMNI_IMAGE_TAG` default in `docker-compose.yml`,
   `docker-compose.hwaccel.yml` and `.env.example`.
2. Move the `Unreleased` entries under a new `## [X.Y.Z-STAGE.N] - YYYY-MM-DD` heading, add the
   stage's *Known gaps* or *Upgrade notes*, and leave `Unreleased` empty above it.
3. Merge that to `main` and wait for CI.
4. Tag the merged commit with the version (no `v`) and push the tag.

The tag starts `.github/workflows/release.yml`. It refuses a tag the source disagrees with (step 1)
or that has no section in this file (step 2), then builds and publishes every image on GitHub
Container Registry, stamped with the version, the commit and the build date, and creates the GitHub
release from this file's section, as a *pre-release* for any `alpha`, `beta` or `rc`, with the
compose files and `.env.example` attached.
