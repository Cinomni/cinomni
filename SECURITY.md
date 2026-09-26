# Security

This document is for contributors. It describes how to report a vulnerability and the security
practices every change must follow. It is self-contained.

## Reporting a vulnerability

**Do not open a public issue for a security problem.** Report it privately to the maintainers
(open a private GitHub security advisory on this repository, or contact a maintainer directly).
Please include what you found, how to reproduce it, and the impact. We'll acknowledge and work
a fix before any public disclosure.

## What we defend against

Cinomni is self-hosted, but it constantly handles **untrusted input** and drives **untrusted
external systems**. Treat all of the following as hostile until validated:

- Responses from indexers and metadata/subtitle providers (HTML, XML, JSON).
- The BitTorrent swarm and the content it delivers.
- Media files handed to `ffprobe` / FFmpeg, and subtitle files written to disk.
- Every value in an HTTP request from a client (paths, segment names, ids, capabilities).

The main risks we actively design against are **SSRF** (server-side request forgery), **path
traversal**, **command/argument injection** into external processes, **XXE** in XML parsing,
**credential leakage**, and **auth bypass / account enumeration**.

## Practices every change must follow

### Outbound HTTP (indexers, subtitles, metadata, `.torrent` links)
- Go through the SSRF-hardened client built on `SsrfGuard` (`Cinomni.Kernel.Net`). It resolves
  DNS and validates the **real connected IP** (anti-rebinding), blocks private/loopback/link-local
  ranges, disables redirects and proxies, and bounds the response size.
- Parse hostile XML with `DtdProcessing.Prohibit` and `XmlResolver = null` (anti-XXE). Parse JSON
  defensively; never `eval`-style dynamic execution.
- **Never** bypass `SsrfGuard` or add an unguarded `HttpClient` for third-party calls.
- An indexer catalog source is an administrator-chosen URL whose manifest decides which sites
  Cinomni will query and how. Cinomni ships none. The URL must be https with a public host; the
  manifest is fetched on `SsrfSafeHttpHandler` with no redirects, a 30 s timeout and a 4 MiB cap
  counted while reading, and is validated whole before anything is stored: bounded entry count and
  string lengths, unique keys, public http(s) base URLs, every definition through
  `IndexerDefinitionParser` with strict selectors, and no `session` login block. An invalid manifest
  is rejected entirely and the previous snapshot is kept. Refresh outcomes carry fixed messages and
  status codes, never the response body; the fetch exception goes to the operator log only.
- FlareSolverr is a separate hostile-content boundary. In production it has only the internal
  `indexer-control` network and explicitly uses `indexer-egress` for each browser request. The guard
  rejects non-public IPv4 and IPv6 DNS answers and opens the outbound socket to the validated numeric
  address. Its derived image disables Chrome's implicit loopback proxy bypass. Never attach
  FlareSolverr to `edge`, remove that browser flag, set ambient proxy variables, or bypass the guard.
- A login indexer with FlareSolverr on runs **cleared**: the browser only solves the site's challenge,
  and the sign-in, searches, detail pages and the member-only `.torrent` go out as plain requests on
  the `IndexerClearance` client, which sends them through `indexer-egress` (the clearance is bound to
  that address). On that path the guard, not `SsrfSafeHttpHandler`, enforces public destinations; the
  client still checks `SsrfGuard` and exact same-origin before every request and follows no redirect.
  Only challenge-family cookies (`cf_*`, `__cf*`, `_cf*`) scoped to the host are kept, in memory only,
  and are stripped from the session jar stored at rest.
- Session cookies never leave Discovery. Downloads asks `IReleaseFileSource` for a private tracker's
  file and receives bytes or a magnet. Discovery answers only for an enabled indexer that owns the
  link's exact https origin **and** returned that exact link from its own search; anything else is left
  to the anonymous fetch. The file is fetched at add time only, since a private tracker may count it as a
  download by the member.

### External processes (ffprobe, FFmpeg, the sidecar)
- Launch with `ProcessStartInfo.ArgumentList` (**argv**), `UseShellExecute = false`; never build
  a shell command line from interpolated input.
- Use a closed **whitelist** of codecs/arguments; apply timeouts; drain `stdout` **and** `stderr`
  to avoid pipe deadlocks. Degrade gracefully when the tool is missing.
- A long-running child (FFmpeg for HLS) has an **owner** from the moment it starts: Playback's
  `ActiveTranscodes` holds every process, bounds how many run (per node and per account, admitted
  under one lock before anything is spawned, and nothing once the host is stopping), stops the
  process tree when its session ends or goes silent, and stops them all on shutdown. Its stderr is
  drained for its whole life into a bounded tail (`BoundedLineTail`), which may quote the media path,
  so it is stored on the transcode job row in the database and nowhere else: never in a log line,
  a response, an integration event or a metric, which carry the exit code.
- Ownership outlives a crash. In the packaged image the Host is PID 1, so a crash takes every FFmpeg
  with it; run any other way, a crashed Host can leave one running. The job row records each
  process's id and start time, and the next start stops a process only when every independent fact
  agrees (that id, that start, the configured FFmpeg binary, and on Linux its own command line
  naming that session's output directory), and then only that process, never a tree
  (`OrphanedTranscodeTerminator`). A row is not proof: an id the operating system has handed to
  something else, or a tampered row, meets a process that fails one of those checks and is left
  alone. One that matches but cannot be stopped keeps its row and is retried.
- A stream cannot hold its slot for ever: access to the title is checked again on every progress
  report (a viewer who lost it has the session closed at once), and every stream ends at
  `Playback:MaxTranscodeLifetime` however busy its client keeps it.
- The transcode root is a directory Playback deletes in on its own initiative, so it must not share
  a tree with the library or the staging area: startup refuses it (`TranscodeRootStartup`), and a
  session-named directory no row names is removed only when it holds nothing but HLS output.
- The libtorrent sidecar is a separate process; in production it is intended to run inside a VPN
  network namespace with no published ports.
- The opt-in qBittorrent engine is somebody else's process: Cinomni cannot see or enforce its network
  settings, so the egress guard fails closed when a tunnel is configured. What Cinomni sends it is
  bounded: a magnet is never passed as it arrived but rebuilt from its info-hash, a separator-free
  display name and announce URLs that pass the sidecar's tracker rule (plus no `@`, `#` or `\`,
  where .NET and the torrent engine parse a URL differently); a fetched `.torrent` is capped at
  8 MiB; the save path is pinned (`autoTMM=false`); and a torrent the household added to the client
  itself is never adopted, only one that already saves into the staging area.

### Filesystem
- Confine every path to its allowed root with `PathGuard` (`Confine` / `SanitizeName`): reject
  `..`, UNC (`\\`), device paths, reserved Windows names, and over-length paths. `Confine` also
  resolves every symbolic link along the existing part of both paths and refuses a candidate whose
  resolved form leaves the resolved root, since a torrent can create links in staging. The import scan does
  not follow links at all, into directories or to files. `PathGuard` is
  **module-local to Import** (`src/Modules/Import/Cinomni.Import/Files/PathGuard.cs`), not a kernel
  utility; a third module that needs it should lift it into the kernel rather than re-implement it.
- Client-supplied names (e.g. HLS segment file names) are attacker input: validate them against
  a closed vocabulary and confine them to the output directory before touching disk. Playback does
  this inline in `PlaybackStreamer.ResolveHlsFileAsync` (`IsSafeHlsName` + resolved-prefix check).
- Enforce size caps when writing external content (subtitles, resume data).
- A provider credential goes only to that provider's API. OpenSubtitles' `Api-Key` is added per API
  request, never as a default header, so it does not travel to the host a download link names.
- **Never overwrite a household's file.** `IImportFileSystem.MoveAsync` and `HardlinkOrCopyAsync`
  refuse an existing destination that holds anything else, and a failed landing rolls back only what
  it created; the recycle bin picks the next free name instead of writing over a copy it already
  holds; a landing that finds its own earlier work in place (the same file, or the same bytes
  compared whole) leaves the bin alone; a title's folder that another title already has files in
  (per Import's own record first, then Library's) is never shared, so the incoming title lands under
  a folder tagged with its own work; and the path-repair pass moves only true sidecars (`<stem>.…`,
  never another video) and moves nothing when one of them could not follow.

### Secrets & credentials
- **Never hardcode or commit secrets** (API keys, passwords, tokens). The development credentials
  in `docker-compose.dev.yml` are development-only and must never be used in production.
- Production secrets come from the environment or a secret manager. Validate required secrets at
  startup and fail fast if missing.
- **Never log secrets** or full credentials.
- A declarative indexer's session cookie jar is a secret of the same class as its credential, not a
  cache entry: one row per indexer, AES-GCM under the settings store's `SettingsSecretCipher`, with
  the indexer id **and** a SHA-256 fingerprint of the credential that signed in as additional
  authenticated data. Binding to the fingerprint (never a stored column, which a database dump could
  read) means a row written under one credential fails to decrypt under a different or cleared one
  and is treated as no session, so a credential rotation invalidates its session by construction, not
  by anyone remembering to delete a row. The jar never reaches a log, a trace tag, a metric, or an
  `IndexerSummary` projection; only its presence and last-attempt outcome are ever reported.

### Authentication
- Passwords are hashed with **Argon2id** (PHC format); session tokens are opaque (256-bit) and
  only their **SHA-256 hash** is stored, never the raw token.
- Authentication failures return a **generic** error and perform a decoy verification to resist
  user enumeration and timing attacks. Keep it that way.

- **Two-factor authentication is per account and opt-in** (TOTP, RFC 6238). A correct password on an
  enrolled account does not issue a session: it issues a short-lived, single-use **challenge**, and the
  session comes only when that challenge is answered with a code. The password is therefore verified
  once per sign-in however many times somebody fumbles the code, which matters because each
  verification is a 19 MiB Argon2id hash and shares the login rate limit. A challenge dies after five
  codes, which is what bounds guessing a six-digit number; the per-address limit alone would not. Each
  answer takes its attempt by one conditional update before the code is checked, and a recovery code
  is spent the same way, so parallel requests can neither share an attempt nor spend one code twice.
- **The TOTP secret is encrypted at rest** under `CINOMNI_SECRET_KEY`, bound to the account it belongs
  to, because a secret readable from a database dump is the second factor handed over with the first.
  **Recovery codes are hashed, not encrypted, and that asymmetry is deliberate**: if the master key is
  lost or rotated, every generated code becomes unverifiable, and a recovery code is then the only way
  back into the installation. They are minted at confirmation, shown once, and single-use.
- **A stored secret does not turn the factor on; a confirmation does.** An enrollment somebody began
  and abandoned leaves an account signing in exactly as before, rather than locked behind a QR code
  they never scanned. Confirming needs only a session, so it is refused once the factor is on (it
  would otherwise mint a fresh batch of recovery codes for whoever guessed one code), and a pending
  enrollment dies after five codes (`identity.two_factor_enrollment_abandoned`), leaving the password
  as the only way to start another. Each attempt is spent by one conditional update *before* its code
  is checked, so parallel requests cannot share an attempt and slip past the cap.
  Enrolling, confirming and turning the factor off are throttled **per account** (ten requests a
  quarter hour across the three), because a stolen session can arrive from any address and each of
  them either verifies the password again or accepts a six-digit code. Turning the factor off needs
  the password *and* a current code, so a stolen session cannot simply remove the protection over the
  account it belongs to, and it revokes every other session: the account is less protected
  afterwards, so anything left open elsewhere now matters more than it did.
- **There is a documented way back in** when an authenticator and every recovery code are gone: see
  *If you are locked out of an account* in [DEPLOYMENT.md](./DEPLOYMENT.md). It needs database access,
  which is already total control of the installation, so it grants nothing that access did not.

### Authorization
- An installation has **administrators and regular accounts**, so *authenticated* is not *authorized*.
  Every operator route (putting a title in the catalog, indexers, monitoring policy, metadata refresh,
  downloads, imports, delivery channels, accounts) carries
  `.RequireAuthorization(AuthorizationPolicies.Administrator)`. A regular account browses, plays and
  **requests**; an administrator decides.
- The client is not a security boundary. Hiding a button is a courtesy; the gate is on the endpoint.
- `Cinomni.Host.Tests/ApiAuthorizationTests` enumerates the whole surface and fails on any route that is
  not classified, so a new endpoint cannot quietly ship open. Add it to the list, do not delete the test.
- Never take the caller's identity from the request body or query string; read it from the session's
  claims. `Viewer.From(principal)` (`Cinomni.Kernel.Security`) is the one way to resolve it.

### Content access
- A work sits in a **collection**, and a collection is either open to everyone signed in or restricted to
  the accounts an administrator grants. Catalog owns all three facts, so the decision is one query:
  `IContentAccess` is the single authority and **nothing re-implements it**.
- A read model that answers for a person takes a `Viewer` (`ICatalogBrowse`, `LibraryBrowse`,
  `SubtitleBrowse`); the unscoped twin (`ICatalogQuery`, `ILibraryQuery`, `ISubtitleQuery`) exists only
  for event and command handlers, which answer for the system. `ScopedReadModelTests` fails the build if
  an endpoint binds the unscoped one.
- **Hidden reads exactly like missing.** A work you may not see is absent from the list, 404 by id, 404 by
  external id, and `playback.asset_not_found` when you try to play it, never a distinct "forbidden",
  which would be an oracle.
- **Revocation is enforced on read, not by session teardown.** Playback re-checks access on every file it
  serves, so taking a collection away stops a stream already in flight at the next segment or range
  request. A session with no work recorded serves nothing (fail closed).
- **It applies to everything that names a work, not only to browsing.** The notification inbox, "next up",
  a member's request list and the "already in your library" answer to a new request all consult
  `IContentAccess`. A module that may not depend on Catalog (Metadata) does not answer members about works
  at all: its snapshot routes are administrator-only.
- **A session token in a URL is accepted only by `GET`/`HEAD` of `/api/playback/sessions/{id}/stream` and
  `/api/playback/sessions/{id}/hls/{file}`**: what a `<video>` element fetches without being able to set
  a header. Every other route, the live-update stream included, requires `Authorization: Bearer`. A URL
  ends up in proxy and access logs; the header does not. A playlist fetched that way (a browser that
  plays HLS itself, Safari) comes back with the same token appended to each segment name, sent
  `Cache-Control: no-store`; with an `Authorization` header present nothing from the query is echoed,
  since the header is what authenticated. Each segment request of such a session therefore carries the
  token in its URL too.
- **The live-update stream re-authenticates every 15 seconds** with a fresh run of the sign-in check and
  closes when the session is gone or the account's permissions changed, so logout, revocation, disabling
  and demotion reach an open stream too. One account may hold at most 8 streams.

### Input validation
- Validate at every boundary. Never trust external data (provider responses, file contents,
  request bodies). Fail fast with clear, non-leaking error messages.

## What an internet-facing deployment does not get from Cinomni

Everything above describes an application that assumes it is reached over loopback or from a trusted
network, which is what `CINOMNI_BIND_ADDRESS=127.0.0.1` makes the default. Publishing one to the
internet needs more than that. What follows is what this application does and does not do about it,
written down because a gap nobody records is indistinguishable from one nobody noticed.

- **Only the credential routes are throttled.** `POST /api/identity/login` and
  `POST /api/identity/setup` are rate-limited per client address: ten requests a minute by default,
  configurable from the console, answering 429 with a `Retry-After`. That covers the one surface an
  unauthenticated caller can spend real resources on: every login attempt costs a full Argon2id
  verification, and the decoy that defeats account enumeration means a username that does not exist
  costs the same 19 MiB as one that does. The second-factor routes a signed-in account uses to enroll,
  confirm and turn off the factor are limited per account instead, by a second limiter that runs after
  authentication, with its own concurrency backstop. **Nothing else on the API is throttled**, so an
  authenticated account can still make unbounded requests. Session tokens are 256-bit and not worth
  guessing; the password was the target, and every route that verifies it is now bounded.
  <br>An IPv6 client is counted by its /64, not its address: a single host is routed at least that
  much, so counting per address would hand one attacker 2^64 budgets and permit every request. A
  shared concurrency backstop bounds how many verifications run at once regardless of how many
  addresses appear, because per-address limiting cannot bound a cost that is concurrency rather than
  rate.
  <br>The limit counts the *connecting* address unless `Security:TrustedProxies` names your reverse
  proxy, **and unless that proxy writes `X-Forwarded-For` itself**. A proxy that relays a
  client-supplied header lets any caller choose their own bucket, and that is indistinguishable from
  a legitimate one at this end. Behind an undeclared proxy the opposite happens: every client arrives
  as the proxy, shares one bucket, and the limit becomes an outage for the household while
  restricting the attacker no more than anyone else. Both halves are deployment requirements, not
  optional configuration; see [DEPLOYMENT.md](./DEPLOYMENT.md).
- **Every response carries a content security policy and the usual hardening headers**
  (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy: no-referrer`,
  `Cross-Origin-Opener-Policy`). The policy matters here more than it usually would: the session
  token lives in `localStorage`, so any script running on this origin can read it and use it from
  anywhere. `script-src 'self'` with neither `unsafe-inline` nor `unsafe-eval` is what stands between
  an injected string and that token, and `connect-src 'self'` bounds where anything that did run
  could send it. `Referrer-Policy: no-referrer` is not decoration either: artwork is fetched from
  the metadata provider's image host, which would otherwise be told the URL of every page a viewer
  is on.
  <br>Two directives are looser than they look, both knowingly: `img-src` allows any HTTPS origin,
  because a poster is a URL at whichever provider is configured and no fixed allowlist could stay
  correct; and `style-src` allows `unsafe-inline`, because an inline style is ordinary React and the
  failure would be silent and cosmetic. Neither lets anything execute. **HSTS is off unless a deployment
  claims TLS** (`Security:Hsts`), and then sent only on requests that arrived over it. It is not
  inferred from `X-Forwarded-Proto` alone, because that would read a six-month browser commitment out
  of a request header: behind a proxy that relays client headers rather than writing its own, one
  such header would tell every browser to refuse plain HTTP to a plain-HTTP installation, and the
  household would be locked out of its own server until each of them cleared that state. It carries
  neither `includeSubDomains` nor `preload`: those commit a whole domain rather than this
  application, and a media server should not decide that for a subdomain of something else.
- **There is no TLS in the image.** It speaks plain HTTP on 8080, so credentials and the bearer token
  cross the network in the clear unless something in front terminates TLS.
- **First-run setup is a race against whoever reaches the port first.** `POST /api/identity/setup` is
  anonymous, as it has to be, and it creates the administrator for whoever calls it first. An
  installation published before its owner completes setup can be claimed by a stranger.

A reverse proxy supplies all four, which is what makes this a documented deployment requirement
rather than an open defect: see *Publishing beyond loopback* in [DEPLOYMENT.md](./DEPLOYMENT.md) for
what it has to do. Two of them belong in the application eventually (rate limiting on the anonymous
routes, and the response headers) and are on the post-MVP backlog rather than done.

## The short version

Don't disable `SsrfGuard`. Don't shell out. Don't concatenate untrusted input into a path or a
process argument. Don't store or log secrets. Validate everything that crosses a boundary.
