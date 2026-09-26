# Cinomni web — UX/UI audit and redesign

This document records the audit of the SPA as of September 2026, the design system that replaces the
ad-hoc styling, and the phased plan that carries the rest of the app onto it. It is the reference for
any change to layout, tokens, navigation or the shared media components. Product scope lives in
[README.md](../README.md) and [ROADMAP.md](../ROADMAP.md); this file only describes the presentation layer.

The guiding rule: **the content is the interface.** Artwork builds the hierarchy; containers are the
exception, not the default. A screenshot of any household-facing page must not be mistakable for a
CRM, analytics panel or B2B admin.

---

## 1. Audit of the current state

Measured before the redesign: 23 feature files wrap their content in `Card` (a bordered, rounded
surface); 11 use `rounded-xl` and 6 `rounded-2xl` with no rule for which; 229 text sizes are
`text-sm`/`text-xs` against 3 larger ones, so there is effectively no type scale; `--color-faint`
(`#5b6478`) is used for 96 small labels at roughly 3.3:1 contrast.

| # | File / component | Problem | UX impact | Resolution |
|---|---|---|---|---|
| 1 | `App.tsx`, `AppLayout.tsx` | No Home: `/` is the library grid. The first screen is a filtered list with two "Add" buttons. | The app opens like a file manager; nothing says "watch something". | New `HomePage` at `/`; the library moves to `/movies`, `/series`, `/library`. |
| 2 | `AppLayout.tsx` `NAV` | Nine flat entries: *Add movie*, *Add series*, *Administration* and *Your account* sit at the same level as *Library*. | Operator plumbing competes with watching; the nav reads as an admin panel. | Two groups: **Watch** (Home, Movies, Series, Upcoming, Requests) and a quiet **Manage** group (Activity, Downloads, Administration). Adding is an action on Movies/Series and in search, not a destination. |
| 3 | `AppLayout.tsx` mobile header | Mobile nav is a horizontally scrolling strip of unlabeled icons at the top. | Nine icons with no labels; destinations are guesswork on a phone. | Bottom tab bar (Home, Movies, Series, Upcoming, More) with labels, search in the slim top bar; the rest in a "More" sheet. |
| 4 | `AppLayout.tsx`, `AuthPage.tsx` | Brand is an amber square with a generic film icon — the most common "AI dashboard" logo shape — although an official mark exists. | Nothing recognizable. | The official Cinomni mark (open C around a six-blade shutter) inline in `ui/Brand.tsx`, plus favicon, touch icons and manifest in `public/`. |
| 5 | (missing) | No global search. Title filtering exists only inside the library page. | The most common intent ("is X here? can I get it?") needs two pages. | `SearchOverlay` on `/` and `Ctrl/⌘+K`, local titles first, providers second, pages last. |
| 6 | `index.css` | Only colors are tokens; no type scale, radius scale, motion or elevation tokens. `--color-warning` is the same amber as the accent. | Warning states read as primary actions; every page invents its own sizes. | Full token set (§4). Warning moved to orange so it is never confused with the accent. |
| 7 | `index.css` `--color-faint` | ~3.3:1 contrast on the page background for small text. | Fails WCAG AA on every date, count and hint. | Raised to `#7a8397` (≈4.7:1 on `bg`). |
| 8 | `WorkHero.tsx` | The hero is a bordered card with a 40%-opacity backdrop inside the padded page column. | The artwork is a dim texture behind a box; the page reads as CRUD. | Full-bleed `MediaHero`: backdrop edge to edge, controlled scrim, display title, metadata line, one contextual CTA. |
| 9 | `WorkDetailPage.tsx` | Five same-weight buttons in the hero (Play, Monitor, Find releases, Refresh metadata, Change artwork); raw provider ids as chips under the title. | The one action a viewer wants is one of five; internal ids are the second thing read. | One primary CTA, monitor as an icon toggle, the rest in an overflow menu. Ids move to the *Advanced* disclosure. |
| 10 | `WorkDetailPage.tsx` `AssetPanel` | File path, stream codecs and release group are the first section under the hero, as badges. | Technical data dominates a viewer's page. | *Experience* layer: a one-line quality summary (`2160p · HEVC · HDR · 5.1`). *Advanced* layer (`<details>`): path, versions, streams, release group, subtitles, ids. |
| 11 | `PosterCard.tsx` | Border + `rounded-xl` + drop shadow on every poster; availability is a 10px color dot with a tooltip. | Posters look like cards; state is color-only and invisible on touch. | `MediaPoster`: borderless print with a hairline inner edge, status as glyph + text, progress along the bottom edge. |
| 12 | `LibraryPage.tsx` | Kind filter, sort select (with a visible label), text filter and collection chips are all permanently expanded above the grid. | Four controls before the first poster. | Prominent search field, compact sort and density toggle, filters on demand in a panel. |
| 13 | `LibraryPage.tsx` | Full-DOM grid for any library size. | Large libraries scroll poorly. | `content-visibility: auto` on poster tiles (no dependency). True virtualization waits on API paging. |
| 14 | `SeasonAccordion.tsx` / `EpisodeRow.tsx` | Each season is a bordered box; each episode shows a badge, play, search and a switch at all times. | A 20-season show is a wall of boxes and switches. | Seasons as open rows with a progress meter; episode secondary actions (find releases) in a context menu; play and state stay visible. |
| 15 | `Badge.tsx`, `lib/status.ts` | Status is a colored pill everywhere; the same state has different words on different pages ("In library" / "Available" / "Completed"). | Color-only meaning; no shared vocabulary. | `MediaStatus` + `StatusGlyph`: one vocabulary, a distinct shape per state, text always present. |
| 16 | `ui/Card.tsx`, `DownloadsPage`, `ActivityPage`, `CalendarPage`, console pages | Every list row is a bordered card; `CalendarPage` puts a card around the whole page. | Boxes inside boxes; the pipeline is not visible. | Phase 3: rows separated by hairlines, pipeline stepper for acquisition. |
| 17 | `EmptyState.tsx` / `ErrorState.tsx` | Dashed-border box; errors show a red ✕ and the raw message as the headline. | Errors look alarming and technical. | Borderless states; a plain-words headline, the narrowed message as explanation, and an optional `details` slot behind "Technical details" for codes and statuses. |
| 18 | `LoadingBlock.tsx` | A centered spinner is the universal loading state. | Layout jumps when content arrives. | `Skeleton` primitives shaped like posters, rails and heroes. |
| 19 | `ConsoleLayout.tsx` | Eleven peer tabs in one scrolling strip. | Hard to scan; no grouping by domain. | Phase 4: grouped sections (Library, Acquisition, Playback, People, System). |
| 20 | `Modal.tsx` | Search, attempts, download details and removal all open modals. | Context is lost for read-only detail. | Phase 3: detail panels become side sheets; confirmations stay modal. |

## 2. Prioritized problems

1. **No media-first entry point** (1, 8, 11) — the app does not look like a media app. *Critical.*
2. **Navigation mixes watching and operating** (2, 3, 19) — highest cognitive cost for a household member.
3. **No design system beyond colors** (6, 7, 15) — the root cause of per-page drift, color-only status and failing contrast.
4. **Technical data leads on viewer pages** (9, 10, 14).
5. **Search is buried** (5, 12).
6. **Boxes everywhere** (16, 17) — the visual signature of a generated admin template.
7. **Loading and error states** (17, 18).

## 3. Navigation architecture

```
Watch                     Manage (administrators)        Personal
  Home         /            Activity      /activity         Account  /account
  Movies       /movies      Downloads     /downloads        Sign out
  Series       /series      Administration /console
  Upcoming     /calendar
  Requests     /requests
                                                          Search: "/" or Ctrl/⌘+K, everywhere
```

- **Desktop (≥ 768px):** a 232px sidebar. Watch group unlabeled at the top; *Manage* under a small
  label; search trigger above the groups; account and notifications at the bottom.
- **Mobile (< 768px):** a slim top bar (brand, search, notifications) and a bottom tab bar
  (Home, Movies, Series, Upcoming, More). *More* opens a sheet with the remaining destinations.
- Adding a title is an action, not a place: *Add movie* on Movies, *Add series* on Series, the empty
  states, and the "search providers" row of the search overlay. `/add` and `/add/series` keep working.
- `/library` stays (all kinds, with the kind filter); old `/` bookmarks now land on Home.

## 4. Visual system and tokens

All tokens are in `src/index.css` (`@theme`). Components must not introduce raw colors or sizes.

**Color** — semantic roles, dark only:

| Token | Value | Role |
|---|---|---|
| `bg` | `#0a0c10` | Page ("the room") |
| `surface` | `#12151c` | Sidebar, sheets, inputs at rest |
| `elevated` | `#1a1f29` | Menus, dialogs, pressed |
| `hover` | `#212735` | Hover fill on quiet controls |
| `line` / `line-soft` | `#2a3140` / `#1c212c` | Hairlines only — never a box around content |
| `fg` / `muted` / `faint` | `#eceff5` / `#9aa2b5` / `#7a8397` | Text primary / secondary / metadata (all ≥ 4.5:1 on `bg`) |
| `accent` | `#f5b544` (brand amber) | Marquee amber: primary action, active nav, progress. One per view. |
| `success` `warning` `danger` `info` | green / orange / red / blue | State only, always paired with a glyph and text |
| `scrim` | `#0a0c10` | Gradient base for artwork overlays |

**Type scale** (system fonts, by design): `display` 44/48 bold −0.03em · `title` 30/36 semibold ·
`section` 19/26 semibold · `card` 14/20 medium · `body` 15/24 · `meta` 13/18 · `label` 11/14 uppercase
0.08em. Numerals in metadata use `tabular-nums`; episode codes and technical values use mono.

**Spacing:** the Tailwind 4px grid restricted to 1, 2, 3, 4, 6, 8, 12, 16 (4–64px). The page gutter is
the `gutter` utility (16 / 24 / 40px).

**Radius:** three — `media` 6px (artwork), `control` 8px (buttons, inputs, menus), `panel` 14px
(dialogs, sheets). `rounded-full` only for pills and avatars.

**Motion:** `--ease-out-quint`, 120ms (press/hover), 200ms (reveal), 240ms (sheet/dialog). Everything is
disabled under `prefers-reduced-motion`.

### Signature elements (what makes a screenshot Cinomni)

1. **The shutter mark** — the official open-C-and-shutter mark leads the wordmark and the empty/first-run states.
2. **Print-edge posters** — borderless 2:3 artwork, 6px radius, a hairline inner highlight like the edge
   of a photographic print; no drop shadows at rest.
3. **The marquee line** — progress and the active destination are drawn as a 3px amber line: along the
   bottom edge of a poster, the left edge of the active nav item, under the hero CTA.
4. **Ticket metadata** — metadata lines in uppercase `label` type with tabular numerals, separated by
   thin vertical rules (`2016 │ 1H 56M │ RELEASED`).
5. **Shape-coded status** — every state has its own glyph (filled disc, half disc, ring, arrow, clock,
   cross, check), so state survives grayscale and color-blindness.

## 5. Components

Created (`src/components/media/`, `src/ui/`):

| Component | Purpose |
|---|---|
| `MediaPoster` | The poster tile: artwork, hover/focus reveal, status, progress edge, title/year below. Replaces `PosterCard`. |
| `MediaRail` | A titled horizontal row with scroll buttons, keyboard-scrollable, `aria-roledescription`. |
| `MediaHero` | Full-bleed backdrop hero with scrim, eyebrow metadata, title, overview, actions slot. Replaces `WorkHero`. |
| `MediaMetadata` | The ticket-style metadata line. |
| `MediaStatus` / `StatusGlyph` | The status vocabulary (§1 #15). |
| `ProgressEdge` | The marquee progress line. |
| `SearchOverlay` | Global search (dialog + combobox pattern). |
| `Menu` | Accessible overflow/context menu (menu button pattern, arrow keys, Escape). |
| `Skeleton` | `SkeletonPoster`, `SkeletonRail`, `SkeletonHero`. |
| `Disclosure` | The *Advanced* layer (`<details>`-based). |
| `Brand` | Official mark (`ApertureMark`) and wordmark. |

Refactored: `AppLayout` (grouped sidebar, bottom tabs), `LibraryPage`, `WorkDetailPage`,
`SeriesDetailPage`, `SeasonAccordion`, `EpisodeRow`, `EmptyState`, `ErrorState`, `Button` (radius,
motion, `icon-only` size), `Badge` (quieter), `Modal` (radius, motion).

Added in Phase 3–4: `PipelineSteps`, `Modal placement="side"` (side sheet), grouped `ConsoleLayout`. The long settings form has an in-page section index; the unused `ProgressBar` primitive was removed.

## 6. Page proposals

- **Home** — hero (next episode to continue, else newest title with a backdrop), *Continue watching*
  (landscape tiles with the resume position), *Recently added* (large posters), *Upcoming* (a dated
  list, not posters — air dates are the content), *Trending* (administrators: import-list entries that
  resolved to a library title). No KPIs.
- **Movies / Series / Library** — title + count, large search field, sort, density (large/compact),
  *Filters* panel on demand (availability, collection). Grid of `MediaPoster`.
- **Movie** — `MediaHero` with contextual CTA: *Resume at 42:10* › *Play* › (none, state line says why).
  Monitor toggle and overflow menu. Below: *Playback* summary (quality, audio, subtitle languages), then
  *Advanced* disclosure (files, streams, release group, subtitles pipeline, provider ids).
- **Series** — same hero; CTA *Play S02E03*; monitoring policy as a secondary action. Seasons as open
  rows: `Season 2 · 10 episodes · 8 in library` with a meter; episodes with code, title, date, state,
  watched mark; *Find releases* in a per-row menu.
- **Upcoming** (Phase 3) — agenda grouped by day with series artwork, today pinned.
- **Activity / Downloads** (Phase 3) — one *Pipeline* view: per title a stepper
  Search → Grab → Download → Import → Library, rate/ETA on the active step, failures expanded inline
  with the reason in words and "Technical details" behind a disclosure.
- **Administration** (Phase 4) — grouped: *Library* (collections, import list, wanted, imports),
  *Acquisition* (indexers, profiles), *Playback & notifications* (channels), *People* (users),
  *System* (status, operations, settings).

## 7. Implementation plan

| Phase | Scope | Status |
|---|---|---|
| 1 Foundations | Tokens, type scale, radii, motion, status vocabulary, navigation, layouts, search overlay | **Done** |
| 2 Core media | Home, Movies/Series/Library, Movie, Series, Seasons, Episodes | **Done** (upcoming page restyle moves to 3) |
| 3 Operational | Pipeline on Activity (grouped by attention / progress / done, episode named per row), transfer rows on Downloads, side sheets for read-only detail, Upcoming as a dated agenda | **Done** (Activity and Downloads stay two routes, cross-linked) |
| 4 Settings | Administration grouped by domain (System, Library, Acquisition, Notifications, People); shared primitives (`Card`, `Badge`, `DataTable`, `Alert`, fields, `Segmented`) moved onto the tokens so every console page follows | **Done**; the settings form gained an in-page section index |
| 5 Polish | Skeletons everywhere, remaining empty/error copy, responsive audit at 1920/1440/1280/tablet/phone | **Done**: `LoadingBlock` is now skeleton lines, app boot shows the mark, empty states say what to do next |

## 8. Data gaps (backend follow-ups)

The redesign only presents data the API already returns. These need contract changes before the UI can
show them; until then the corresponding UI is omitted rather than faked:

- No trending/discovery source for members (the import list is administrator-only) and no
  recommendation engine; the Home omits those rails for members.
- No quality or language fields on the list route, so the library filters are limited to kind,
  availability, collection and genre.
- The library grid still loads the whole visible catalog: its filters and sorts run client-side, so
  paging it (and virtualization) needs those on the server first. `/api/catalog/works/page` exists.
- `Work` still carries no rating or cast.

Closed (2026-09-23), API and SPA:

- `Work` now carries `runtimeMinutes` and `genres` on every route and `overview` on the detail route
  (`GET /api/catalog/works/{id}`), visible to members. The heroes read them from the work (the
  snapshot-based `useSynopsis` is gone); the Home hero reads the detail route for its synopsis.
  Existing works receive their synopsis on their next metadata refresh.
- `PlaybackProgressView` now carries `durationTicks`, `workId` and `updatedAt`.
- `GET /api/playback/in-progress?limit=` answers *Continue watching*: the caller's started, unfinished
  rows, most recent first, narrowed to titles they may still see (default 20, max 50). Home uses it,
  draws the share watched on each tile, and the player invalidates it when a session ends.
- `GET /api/catalog/works/page?offset=&limit=&collection=` pages the library with `{ items, total,
  offset, limit }` (default 100, max 500). `/api/catalog/works` is unchanged.
- `Work` has `posterSmallUrl` (w342), `backdropSmallUrl` (w780) and `backdropLargeUrl` (w1280);
  `Season` has `posterSmallUrl`. Only TMDB artwork has a size ladder; TVDB/TVMaze urls come back as
  their original. Posters render the small rendition; the hero backdrop is a `srcSet` (780 / 1280 /
  original). A resizing proxy remains an option if the other providers matter. The administrator's
  artwork picker still loads provider originals.

## 9. Post-implementation review (2026-09-23)

Checked against a running API with real data at 1440px and in a 390px frame, plus the automated suite.

- Fixed during review: the home hero said "Latest in your library" for a title with nothing on disk
  (now "Ready to watch" / "Just added"); the backdrop ended in a faint visible edge (scrim now reaches
  solid `bg` before the edge); the back link was illegible over bright artwork (top scrim); the page
  flashed the browser's default background before CSS loaded (`index.html` sets `bg`).
- The three questions: no page in Phase 2 could pass for a CRM — artwork leads Home, library and
  titles; containers only remain where they separate (menus, dialogs, the sidebar). A user familiar
  with self-hosted media tools finds play, monitor, seasons and releases where they expect them, with operator actions one
  menu away.
- Phase 3–4 review (same setup): Activity rows all read "Severance" because a series fans out one
  intent per episode — each row now names its episode or season from the monitored target; pipeline
  stage labels collided at narrow widths and now use sentence case in a wider column.
- Remaining by design: console pages keep `Card` panels (borderless, the surface step separates
  them) because settings and diagnostics are grouped forms, not media; `DataTable` keeps its spinner
  row, where a table-shaped skeleton would not know its columns' content. Backend follow-ups in §8.
