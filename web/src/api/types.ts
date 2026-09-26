// TypeScript mirror of the Cinomni HTTP API wire shapes. Enums travel as their NAME (the Host registers
// a global JsonStringEnumConverter); Guids and DateTimeOffsets are strings; long/int are numbers.

// --- Identity -----------------------------------------------------------------------------------
export interface SetupRequired {
  setupRequired: boolean
}
/**
 * A sign-in that produced a session. `expiresAt` is the session's own expiry.
 *
 * The second step answers with this shape too, and always with `twoFactorRequired: false` — there is
 * no third step — which is why it is the discriminant and not the presence of `token`.
 */
export interface SessionIssued {
  twoFactorRequired: false
  token: string
  expiresAt: string
}

/**
 * A correct password on an account that keeps a second factor. There is no session yet: the password
 * bought a challenge, and only redeeming it with a code produces one.
 *
 * `expiresAt` here is the CHALLENGE's expiry — five minutes — not a session's. The challenge is also
 * single-use and is discarded after several wrong codes, and none of that is observable from the
 * error the second step returns, so a client that wants to explain it has to say so up front.
 */
export interface TwoFactorChallengeIssued {
  twoFactorRequired: true
  challenge: string
  expiresAt: string
  /**
   * The challenge's lifetime as a duration. Count down from when the reply arrived rather than toward
   * `expiresAt`: that instant is the server's clock, and a browser running ahead of it would give up
   * on a challenge that is still good.
   */
  expiresInSeconds: number
}

/**
 * What `POST /api/identity/login` answers with. Both branches are 200: a correct password on an
 * account with a second factor is not a failure, it is half a sign-in. A wrong password is still 401.
 */
export type LoginResponse = SessionIssued | TwoFactorChallengeIssued

/**
 * A started enrolment, which has NOT turned anything on yet — the account still signs in exactly as
 * it did until the code is confirmed.
 */
export interface TwoFactorEnrollment {
  /** The shared secret in base32, for typing into an authenticator by hand. */
  secret: string
  /** The same secret as an `otpauth://` URI, for an authenticator that can consume one. */
  enrollmentUri: string
}
export type UserRole = 'Member' | 'Administrator'

/**
 * What an account may do. The same shape whether an administrator reads it about someone else or an
 * account reads it about itself: `/me` projects all three fields, so there is no narrower session
 * variant to keep separate.
 *
 * `openRequestLimit` has three meanings, not two, and a control that offers only a number silently
 * destroys the third:
 * - `null` — this account names no limit, so the installation's `requests.defaultOpenRequestLimit`
 *   applies, whatever it is now or becomes later. The value of that setting is not sent here, so a
 *   member can be told which case they are in but not what number it resolves to.
 * - `0` — no limit for this account, overriding the installation default.
 * - `n > 0` — at most `n` requests open (pending or approved but not yet delivered) at once.
 */
export interface UserPermissions {
  canRequest: boolean
  requestsAutoApproved: boolean
  openRequestLimit: number | null
  /**
   * The highest classification this account may see, or null when it is not restricted.
   * `contentCeilingApplies` is false when the ceiling was set for a region this installation no
   * longer uses — the restriction is not in effect, and the client must not pretend it is.
   */
  contentCeiling?: string | null
  contentCeilingRegion?: string | null
  contentCeilingApplies?: boolean
}

export interface ContentRatingLadder {
  region: string | null
  certificates: string[]
}
export interface CurrentUser {
  id: string | null
  username: string | null
  isAdministrator: boolean
  role: UserRole
  permissions: UserPermissions
  /**
   * Read from the database rather than from the session's claims, so it is right even for a session
   * that was issued before the account enrolled — which is every session that enrols.
   */
  twoFactorEnabled: boolean
}
export interface UserAccount {
  id: string
  username: string
  role: UserRole
  isAdministrator: boolean
  permissions: UserPermissions
  isDisabled: boolean
  createdAt: string
  lastLoginAt: string | null
}
export interface CreateUserRequest {
  username: string
  password: string
  role: UserRole
  permissions?: UserPermissions
}

// --- Catalog ------------------------------------------------------------------------------------
export type MetadataProviderName = 'Tmdb' | 'Imdb' | 'Tvdb' | 'TvMaze'
export type WorkKind = 'Movie' | 'Series'
export type WorkStatus = 'Unknown' | 'Announced' | 'Released'

export interface ExternalIdDto {
  provider: MetadataProviderName
  value: string
}
export interface Work {
  id: string
  kind: WorkKind
  title: string
  year: number | null
  status: WorkStatus
  hasAsset: boolean
  posterUrl: string | null
  backdropUrl: string | null
  metadataSnapshotId: string | null
  /**
   * Series rollups — all 0 for a movie. `seasonCount` is only populated by the **detail** route:
   * the list route deliberately stays a flat scan, so it reports 0 there even for a series.
   */
  seasonCount: number
  episodeCount: number
  availableEpisodeCount: number
  collectionId: string | null
  /**
   * Held back from members until its metadata arrives: it was added while collection rules exist, and
   * they cannot read its genres or rating yet. Only an administrator ever receives `true`.
   */
  awaitingMetadata: boolean
  externalIds: ExternalIdDto[]
  /**
   * The provider's synopsis. Served by the **detail** route only — the list routes answer for a whole
   * library and leave it null. Null also means the provider has not said.
   */
  overview: string | null
  runtimeMinutes: number | null
  /** Provider genre labels, in the provider's order; empty when none has been published. */
  genres: string[]
  /**
   * Renditions of `posterUrl` / `backdropUrl` sized for what is drawn: 342 px poster, 780 px and
   * 1280 px backdrop. A provider with no size ladder answers its original url here, so these are always
   * safe to render; keep the originals for where full resolution is genuinely needed.
   */
  posterSmallUrl: string | null
  backdropSmallUrl: string | null
  backdropLargeUrl: string | null
}
/** One page of `/api/catalog/works/page`; `total` counts every work that matches, not just this page. */
export interface WorkPage {
  items: Work[]
  total: number
  offset: number
  limit: number
}
export type WorkAvailabilityFilter = 'Complete' | 'Partial' | 'None'
export type WorkSortKey = 'Title' | 'Year' | 'Added'
/** The server-side filters of the paged list; every one optional, combined with AND. */
export interface WorkPageQuery {
  collectionId?: string
  kind?: WorkKind
  /** Matched anywhere in the title, ignoring case. */
  q?: string
  availability?: WorkAvailabilityFilter
  /** A provider genre label, matched exactly. */
  genre?: string
  sort?: WorkSortKey
}
export interface GenreCount {
  genre: string
  count: number
}
/** Counts for the library's filter controls over what the caller may see on one shelf. */
export interface WorkFacets {
  movies: number
  series: number
  /** Genres of the works of the asked kind, most common first. */
  genres: GenreCount[]
}
export interface AddMovieRequest {
  title: string
  year: number | null
  externalIds: ExternalIdDto[] | null
  collectionId?: string | null
}

export type CollectionKind = 'Movies' | 'Series' | 'Mixed'
export type CollectionAccessMode = 'Open' | 'Restricted'

export interface Collection {
  id: string
  name: string
  kind: CollectionKind
  accessMode: CollectionAccessMode
  isDefault: boolean
  workCount: number
  /**
   * Evaluation order across collections: lower runs first, and first match wins. This is what
   * decides which shelf claims a title that matches two rule sets, so it is not a display
   * preference — reordering changes who can see what.
   */
  rulePriority: number
}
export interface CreateCollectionRequest {
  name: string
  kind: CollectionKind
  accessMode: CollectionAccessMode
}

/**
 * The closed vocabulary a rule condition may name. Closed on purpose, and the absences are the
 * interesting part: availability, status and episode counts are NOT fields, because a title is in
 * exactly one collection and collections govern who may see it. A rule on availability would move a
 * title — and change its audience — as a side effect of a download finishing. A restricted shelf
 * that opens itself when an episode lands is a security failure shaped like a feature.
 */
export type CollectionRuleField =
  | 'Kind'
  | 'Genre'
  | 'ContentRating'
  | 'Year'
  | 'RuntimeMinutes'
  | 'OriginalLanguage'
  | 'Title'

export type CollectionRuleOperator = 'Is' | 'IsNot' | 'AtLeast' | 'AtMost' | 'Contains' | 'StartsWith'

/**
 * One condition. Every value is a string on the wire, including the numeric fields — the backend
 * parses per field and refuses what it cannot read rather than coercing it.
 *
 * `values` are alternatives (OR) within a condition; conditions are combined with AND. There is no
 * nesting and no regular expressions, by design: a closed grammar is what lets this client offer
 * only what the API will accept.
 */
export interface CollectionRuleCondition {
  field: CollectionRuleField
  operator: CollectionRuleOperator
  values: string[]
}

export interface CollectionRule {
  id: string
  collectionId: string
  name: string
  conditions: CollectionRuleCondition[]
}

/** One title as a preview reports it, with the shelf it is on now rather than the one it would join. */
export interface RulePreviewWork {
  id: string
  title: string
  year: number | null
  kind: WorkKind
  currentCollectionId: string
  currentCollectionName: string
  /**
   * Where it would end up. Constant across a rules preview — the collection being edited — and
   * per-title in a reorder preview, where each title goes to whichever collection claims it next.
   *
   * A title no rule claims any more falls back to the default collection, which is open. That is an
   * exposure nobody anticipates, because it comes from a title ceasing to match rather than from
   * moving it anywhere, and this field is what makes it countable.
   */
  targetCollectionId: string
  targetCollectionName: string
  pinned: boolean
}

/**
 * What a rule set would do, before it does it. The three counts are not interchangeable and a screen
 * that showed only `matched` would misstate the consequence: a pinned title matches and stays put,
 * and a title already on the target shelf matches and does not move either.
 */
export interface RulePreview {
  matched: number
  wouldMove: number
  pinnedSkipped: number
  /** The first page of affected titles, not the whole set. */
  works: RulePreviewWork[]
}

/** How many titles changed shelf when a rule set was saved. */
export interface RulesApplied {
  moved: number
}
export interface AddSeriesRequest {
  title: string
  year: number | null
  externalIds: ExternalIdDto[] | null
}
/** A season of a series. `airDate` is a provider date (`YYYY-MM-DD`), not an instant. */
export interface Season {
  id: string
  workId: string
  number: number
  title: string | null
  airDate: string | null
  expectedEpisodeCount: number | null
  posterUrl: string | null
  /** A 342 px rendition of `posterUrl` where the provider offers one; otherwise the same url. */
  posterSmallUrl: string | null
}
/**
 * An episode of a series. Both air-date representations travel: `airDate` is the published date and
 * `airDateTime` is only set when the provider supplied a timezone-aware instant.
 */
export interface Episode {
  id: string
  seasonId: string
  workId: string
  seasonNumber: number
  number: number
  absoluteNumber: number | null
  title: string | null
  airDate: string | null
  airDateTime: string | null
  runtimeMinutes: number | null
  hasAsset: boolean
}

// --- Metadata -----------------------------------------------------------------------------------
export type MediaKind = 'Movie' | 'Series'
export type ArtworkKind = 'Poster' | 'Backdrop' | 'Logo'

export interface MetadataCandidate {
  provider: string
  kind: MediaKind
  externalId: string
  title: string
  year: number | null
  overview: string | null
  /**
   * Cross-provider identities a series candidate was de-duplicated on. The list shows one row per
   * show rather than one row per provider, so adding it must carry every identity it is known by.
   */
  tvdbId: string | null
  imdbId: string | null
  tmdbId: string | null
}
export interface MetadataArtwork {
  id: string
  kind: ArtworkKind
  url: string
  /** A rendition sized for a candidate grid; the original `url` when the provider offers none. */
  thumbnailUrl: string | null
  language: string | null
  width: number | null
  height: number | null
  voteAverage: number | null
  voteCount: number | null
  isSelected: boolean
}
export interface MetadataSnapshot {
  id: string
  workId: string
  provider: string
  kind: MediaKind
  externalId: string
  title: string
  originalTitle: string | null
  year: number | null
  overview: string | null
  runtimeMinutes: number | null
  originalLanguage: string | null
  posterUrl: string | null
  backdropUrl: string | null
  fetchedAt: string
  artwork: MetadataArtwork[]
}
export interface RefreshMetadataRequest {
  workId: string
  provider: string
  externalId: string
  kind?: MediaKind
}

// --- Monitoring ---------------------------------------------------------------------------------
export type MonitoringMode = 'None' | 'All' | 'Future' | 'Pilot' | 'FirstSeason' | 'LastSeason' | 'Existing'
export type TargetKind = 'Movie' | 'Season' | 'Episode' | 'Series'

export interface CalendarEntry {
  id: string
  workId: string
  workTitle: string
  kind: TargetKind
  seasonNumber: number | null
  episodeNumber: number | null
  title: string | null
  /** Published air date, `YYYY-MM-DD`. */
  airDate: string
  airDateTime: string | null
  monitored: boolean
  isMissing: boolean
}

export interface ImportListEntry {
  id: string
  provider: string
  externalId: string
  kind: MediaKind
  title: string
  year: number | null
  workId: string | null
  outcome: 'Added' | 'AlreadyKnown'
  firstSeenAt: string
  lastSeenAt: string
}

export interface ImportListView {
  enabled: boolean
  entries: ImportListEntry[]
}

export interface MonitoredTarget {
  id: string
  workId: string
  kind: TargetKind
  monitored: boolean
  mode: MonitoringMode
  isMissing: boolean
  /** The catalog unit the target watches: the work id for a movie, the season/episode id otherwise. */
  targetRef: string
  parentId: string | null
  seasonNumber: number | null
  episodeNumber: number | null
  absoluteNumber: number | null
  airDate: string | null
  title: string | null
  /** Subtree rollups: 0/0 on a leaf, the season's or the series' totals on a branch. */
  missingCount: number
  totalCount: number
}
export interface MonitoredSeasonNode {
  target: MonitoredTarget
  episodes: MonitoredTarget[]
}
/** The whole target tree of one work, as `GET /works/{id}/targets` returns it. */
export interface MonitoredTargetTree {
  root: MonitoredTarget | null
  seasons: MonitoredSeasonNode[]
}

// --- Library ------------------------------------------------------------------------------------
export type AssetState = 'Active' | 'Upgraded' | 'Missing' | 'Removed'
export type StreamType = 'Video' | 'Audio' | 'Subtitle'
export type VideoRangeType = 'Sdr' | 'Hdr10' | 'Hdr10Plus' | 'DoVi' | 'Hlg'

export interface MediaAssetSummary {
  id: string
  workId: string
  state: AssetState
  primaryVersionId: string | null
  createdAt: string
  /**
   * The catalog units this file covers: `[workId]` for a movie, one episode id per episode for a
   * series — a multi-episode file is one asset with several links.
   */
  unitIds: string[]
}
export interface MediaStream {
  streamIndex: number
  type: StreamType
  codec: string | null
  language: string | null
  channels: number | null
  width: number | null
  height: number | null
  bitDepth: number | null
  videoRangeType: VideoRangeType | null
  isDefault: boolean
  isForced: boolean
  isExternal: boolean
}
export interface MediaVersion {
  id: string
  relativePath: string
  /** Server-side path: only present for administrators. */
  fullPath: string | null
  size: number
  releaseGroup: string | null
  streams: MediaStream[]
}
export interface MediaAssetDetail {
  asset: MediaAssetSummary
  targetIds: string[]
  unitIds: string[]
  versions: MediaVersion[]
}

// --- Subtitles ----------------------------------------------------------------------------------
export type SubtitleState =
  | 'Requested'
  | 'Searching'
  | 'Evaluated'
  | 'Downloading'
  | 'Syncing'
  | 'NotFound'
  | 'Available'
export interface SubtitleSearchSummary {
  id: string
  assetId: string
  language: string
  forced: boolean
  hearingImpaired: boolean
  state: SubtitleState
  attempts: number
}

// --- Playback -----------------------------------------------------------------------------------
export type PlaybackMethod = 'DirectPlay' | 'Remux' | 'Transcode'
export type EncoderBackend = 'Software' | 'Vaapi' | 'Nvenc' | 'Qsv'
export type PlaybackState =
  | 'Starting'
  | 'DirectPlaying'
  | 'Remuxing'
  | 'Transcoding'
  | 'Paused'
  | 'Completed'
  | 'Failed'
/** Why a session ended; null while it is open. */
export type PlaybackEndReason =
  | 'Watched'
  | 'Stopped'
  | 'Idle'
  | 'Expired'
  | 'AccessRevoked'
  | 'TranscodeFailed'
  | 'ServerRestarted'
  | 'Recovered'

export interface ClientCapability {
  containers: string[]
  videoCodecs: string[]
  audioCodecs: string[]
  maxHeight: number | null
}
export interface PlaybackDecision {
  property: string
  expected: string
  actual: string
  verdict: string
}
export interface PlaybackPlan {
  method: PlaybackMethod
  transcodeReasons: string[]
  decisions: PlaybackDecision[]
  backend: EncoderBackend
  accelerationReasons: string[]
  decodeAccelerated: boolean
  /** The box and bitrate a transcode converts to; null (or absent on older sessions) where uncapped. */
  targetMaxWidth?: number | null
  targetMaxHeight?: number | null
  targetBitrateKbps?: number | null
  /** What a transcode produces; absent on sessions planned before these were recorded. */
  outputCodec?: 'H264' | 'Hevc' | null
  toneMapped?: boolean
  burnInSubtitleIndex?: number | null
  audioChannels?: number | null
}
export interface TrackSelection {
  audio: number | null
  subtitle: number | null
}
export interface AudioTrack {
  index: number
  language: string | null
  codec: string | null
  channels: number | null
  isDefault: boolean
}
export interface SubtitleTrack {
  index: number
  language: string | null
  codec: string | null
  isForced: boolean
  isDefault: boolean
  isExternal: boolean
  /** Where the track is served as WebVTT; null for a picture track (PGS, VobSub) the server cannot serve. */
  url: string | null
  /** A picture track this server draws into the video when the viewer picks it (a new session). */
  canBurnIn?: boolean
}
/** A quality the server offers. `original` (all ceilings null) is the file as it is. */
export interface QualityOption {
  id: string
  maxWidth: number | null
  maxHeight: number | null
  maxBitrateKbps: number | null
}
/**
 * The tracks, qualities and timeline of a session. `streamOffsetTicks` is where the stream's own zero
 * sits in the file (a conversion that started part way through); `durationTicks` is the whole file.
 */
export interface PlaybackMedia {
  audioTracks: AudioTrack[]
  subtitleTracks: SubtitleTrack[]
  qualities: QualityOption[]
  quality: string
  streamOffsetTicks: number
  durationTicks: number | null
  /** The picture subtitle this stream carries in its video, if any. */
  burnedInSubtitle?: number | null
}
export interface PlaybackTicket {
  sessionId: string
  method: PlaybackMethod
  streamUrl: string
  resumePositionTicks: number
  selection: TrackSelection
  plan: PlaybackPlan
  media?: PlaybackMedia | null
}
/** What the viewer chose when opening a session; anything left out is the server's choice. */
export interface PlaybackPreferences {
  audioStreamIndex?: number
  subtitleStreamIndex?: number
  subtitlesOff?: boolean
  quality?: string
  startPositionTicks?: number
}
/** One test the hardware probe ran against the real device. */
export interface HardwareProbeTest {
  backend: string
  kind: 'Encode' | 'Decode'
  codec: string
  passed: boolean
  failure: string | null
}
/** What this host can transcode with, as the last hardware probe found it. */
export interface HardwareReport {
  platform: string | null
  ffmpegVersion: string | null
  probedAt: string | null
  hwaccels: string[]
  backends: { backend: string; encodes: string[]; decodes: string[] }[]
  softwareHevc: boolean
  toneMapping: boolean
  subtitleOverlay: boolean
  tests: HardwareProbeTest[]
}
export interface RequestPlaybackBody {
  assetId: string
  capability: ClientCapability
  preferences?: PlaybackPreferences
}
export interface PlaybackSessionDetail {
  session: {
    id: string
    userId: string
    assetId: string
    state: PlaybackState
    method: PlaybackMethod
    positionTicks: number
    endReason: PlaybackEndReason | null
  }
  selection: TrackSelection
  plan: PlaybackPlan
}
export interface ProgressBody {
  positionTicks: number
  durationTicks: number
  isPaused: boolean
}
export interface PlaybackProgressView {
  assetId: string
  positionTicks: number
  played: boolean
  playCount: number
  /** The catalog unit the row belongs to; null for rows written before the series slice. */
  unitId: string | null
  /** The runtime the player last reported; 0 until a report carried one. */
  durationTicks: number
  /** The catalog work the row belongs to; null for rows written before the series slice. */
  workId: string | null
  updatedAt: string | null
}
/** The episode a viewer should watch next in a series. Null for a movie. */
export interface NextUpEpisode {
  workId: string
  unitId: string
  assetId: string
  seasonNumber: number
  episodeNumber: number
  episodeTitle: string | null
  resumePositionTicks: number
}

// --- Downloads ----------------------------------------------------------------------------------
export type DownloadState =
  | 'Queued'
  | 'ResolvingMetadata'
  | 'Checking'
  | 'Downloading'
  | 'Completed'
  | 'Seeding'
  | 'Removed'
  | 'Paused'
  | 'Error'
export type FilePriorityLevel = 'Skip' | 'Normal' | 'High' | 'Top'

export interface DownloadTaskSummary {
  id: string
  infoHash: string
  name: string
  state: DownloadState
  progress: number
  downloadRate: number
  uploadRate: number
  numPeers: number
  numSeeds: number
  /**
   * Paused because torrent traffic is not verifiably leaving through the tunnel, rather than because
   * someone paused it. Deliberately not a `DownloadState` member: the state name drives this client's
   * vocabulary, and a held download is a paused download whose reason is recorded on its history.
   */
  networkHeld: boolean
  /**
   * Every acquisition intent waiting on this task, each once — more than one when goals picked the
   * same torrent (a season pack). It is how Activity puts a transfer under the title it serves.
   */
  intentIds: string[]
}
export interface DownloadFile {
  index: number
  path: string
  size: number
  priority: FilePriorityLevel
}
export interface StateHistoryEntry {
  seq: number
  from: string
  to: string
  trigger: string
  occurredAt: string
  note: string | null
}
export interface DownloadTaskDetail {
  task: DownloadTaskSummary
  files: DownloadFile[]
  history: StateHistoryEntry[]
}
/** What an installation knows about where its torrent traffic goes. Administrator-only. */
export type TunnelLossPolicy = 'Block' | 'PauseAndAlert' | 'Ignore'
export interface TunnelEgressStatus {
  /** False is the ordinary, supported state: no tunnel, so every field below is inert. */
  configured: boolean
  policy: TunnelLossPolicy
  verified: boolean
  /**
   * A machine-readable cause, never a rendered sentence. This client translates it; it is never
   * shown raw, which is why the whole vocabulary is listed rather than the common half of it — a
   * value missing here reaches an operator as the machine name they were promised they would not see.
   *
   * Observed by the sidecar: `tunnel-egress-verified`, `tunnel-device-missing`,
   * `tunnel-device-has-no-address`, `default-route-not-via-tunnel`, `egress-identity-not-the-tunnel`,
   * `ipv6-egress-not-the-tunnel`, `no-egress-route`, `tunnel-guard-disabled`,
   * `tunnel-guard-observation-failed`.
   *
   * Concluded by the backend: `sidecar-unreachable`, `not-yet-observed`, `tunnel-observation-stale`,
   * `tunnel-policy-divergent`, `tunnel-device-divergent`, `tunnel-guard-removed`.
   */
  reason: string
  tunnelDevice: string
  heldTaskCount: number
  transitionSequence: number
  observedAt: string | null
}

// --- Acquisition --------------------------------------------------------------------------------
export type IntentState =
  | 'Requested'
  | 'Planned'
  | 'Searching'
  | 'CandidateSelected'
  | 'Downloading'
  | 'Importing'
  | 'Available'
  | 'Exhausted'
  /** The work was removed from the catalog; the goal ended there. */
  | 'Cancelled'
export interface AcquisitionIntentSummary {
  id: string
  targetId: string
  workId: string
  state: IntentState
  attemptCount: number
  maxAttempts: number
  selectedReleaseGuid: string | null
}

// --- Decision -----------------------------------------------------------------------------------
export type Verdict = 'Accepted' | 'RejectedPermanent' | 'RejectedTemporary'
export type ReasonOutcome = 'Pass' | 'Fail'
export type RejectionKind = 'Permanent' | 'Temporary'
export type ReleaseProtocol = 'Torrent' | 'Usenet'

/** One line of a decision's explanation: what was checked, what was expected, what was found. */
export interface EvaluationReason {
  rule: string
  property: string | null
  profileValue: string | null
  actualValue: string | null
  outcome: ReasonOutcome
  rejection: RejectionKind | null
}

/** A release an interactive search found, with the profile's verdict on it and why. */
export interface EvaluatedCandidate {
  evaluationId: string
  releaseGuid: string
  releaseTitle: string
  indexerName: string
  protocol: ReleaseProtocol
  sizeBytes: number
  seeders: number | null
  /** Peers downloading without a complete copy; null when the indexer does not say. */
  leechers: number | null
  publishedAt: string | null
  verdict: Verdict
  customFormatScore: number
  /** How many requested units it covers — 1 for a movie or episode, N for a season pack. */
  episodeCoverage: number
  /** The one the automatic pipeline would have taken. Marks the default; it does not choose. */
  isRecommended: boolean
  reasons: EvaluationReason[]
  /** Set when an operator blocked this release. The sweep will not take it until it is unblocked. */
  blocked: boolean
  blockId: string | null
  blockReason: string | null
  /**
   * Whether grabbing it goes against the profile. False for a release rejected only because it was
   * blocked: once unblocked, taking it overrides nothing. The server decides; the row only asks twice.
   */
  grabOverridesVerdict: boolean
}

export interface ReleaseBlock {
  id: string
  releaseGuid: string
  releaseTitle: string
  reason: string
  createdAt: string
}

export interface ReleaseBlockList {
  blocks: ReleaseBlock[]
  truncated: boolean
}

/** A past evaluation, as the explainability endpoint returns it. */
export interface ReleaseEvaluation {
  id: string
  releaseGuid: string
  releaseTitle: string
  verdict: Verdict
  customFormatScore: number
  reasons: EvaluationReason[]
  /** Read back from the search that found it; null once that search's results are gone. */
  indexerName?: string | null
  seeders?: number | null
  leechers?: number | null
}

export interface InteractiveSearchResult {
  targetId: string
  workId: string
  searchId: string
  term: string
  /** What was searched for, as a person reads it ("Season 2", "S02E05", "Movie"). */
  label: string
  candidates: EvaluatedCandidate[]
}

/** Echo of a hand-picked release, so the UI can say what it just queued. */
export interface ManualSelection {
  evaluationId: string
  targetId: string
  releaseTitle: string
  verdict: Verdict
  overrodeVerdict: boolean
}

// --- Discovery ----------------------------------------------------------------------------------
export type IndexerProtocol = 'Torznab' | 'Newznab' | 'Definition'
/** A raw release an indexer returned, deliberately unparsed — Discovery federates but never judges quality. */
export interface ReleaseCandidate {
  guid: string
  title: string
  downloadUrl: string
  protocol: ReleaseProtocol
  sizeBytes: number
  seeders: number | null
  leechers: number | null
  publishedAt: string | null
  indexerName: string
  seasonNumber: number | null
  episodeNumber: number | null
  tvdbId: string | null
  category: string | null
}
/**
 * What an indexer says it can answer. An indexer that never had capabilities configured reports the
 * permissive shape — both searches supported, no category restriction — which is what the search
 * behaved like before capabilities existed.
 */
export interface IndexerCapabilities {
  supportsMovieSearch: boolean
  supportsTvSearch: boolean
  movieCategories: number[]
  tvCategories: number[]
  movieSearchParams: string[]
  tvSearchParams: string[]
}
/**
 * Whether a secret is stored for an indexer *and whether this installation can read it*. Those are
 * different facts: a master key that is absent, rotated or lost leaves the stored bytes intact and
 * undecryptable. Only 'Readable' authenticates a search — every other state, including the three
 * that still hold a stored secret, means the indexer is queried anonymously.
 *
 * Each name is a deployment fact, never a value: the secret itself is never sent to this client.
 */
export type IndexerCredentialState = 'None' | 'Readable' | 'MasterKeyMissing' | 'MasterKeyChanged' | 'Corrupt'

/**
 * Whether a definition-backed indexer's declared login has actually produced a kept session. A
 * session signs in with the stored credential and is re-created by the next search whenever the site
 * expires it — 'Active' says a session exists now, not that it will still work later.
 */
export type IndexerSessionState = 'None' | 'NotLoggedIn' | 'Active' | 'Failed'
export interface IndexerSummary {
  id: string
  name: string
  protocol: IndexerProtocol
  catalogKey: string | null
  catalogVersion: number | null
  /** The catalog source it was installed from; null for a manual indexer or once that source is removed. */
  catalogSourceId: string | null
  baseUrl: string
  priority: number
  enabled: boolean
  settings: IndexerSettings
  lastTestedAt: string | null
  lastTestSucceeded: boolean | null
  lastTestCode: string | null
  lastTestMessage: string | null
  capabilities: IndexerCapabilities
  /** The declarative definition this indexer runs on. Set only for the 'Definition' protocol. */
  definitionId: string | null
  /** The account this indexer authenticates as. An identifier, not a secret; null when there is none. */
  credentialUsername: string | null
  /**
   * Whether a secret is stored and whether this installation can decrypt it. There is deliberately
   * no field carrying the secret itself — it is never sent to the client, so this state is all the
   * UI can know about it.
   */
  credentialState: IndexerCredentialState
  /**
   * Whether this indexer's definition declares a login block at all. `sessionState` cannot answer it
   * — 'None' is also what a declared login with an unreadable credential reports — so read this, not
   * that, before claiming an indexer needs no session.
   */
  declaresLogin: boolean
  /**
   * Whether the indexer's declared login actually produced a kept session. 'None' covers both an
   * indexer that needs no login and one whose credential cannot be read — the credential state
   * alongside it says which. The cookies behind a session are never sent to this client.
   */
  sessionState: IndexerSessionState
  /** When the kept session was captured, or null while there is none. */
  lastLoginAt: string | null
}
/**
 * `username` is omitted for an API key. With one, `secret` is a password: a `Definition` indexer
 * submits both to its declared login form; Torznab/Newznab send them as HTTP Basic, which the API
 * only accepts for an https base URL (`discovery.insecure_credential`).
 */
export interface SetIndexerCredentialRequest {
  secret: string
  username?: string | null
}
/**
 * Every field is optional on the wire, but this is NOT a patch: the endpoint builds a complete
 * capabilities record from what it receives, so an omitted field is overwritten with that record's
 * default (both flags true, every list empty) rather than left as it was. Always send the whole shape.
 */
export interface SetIndexerCapabilitiesRequest {
  supportsMovieSearch?: boolean
  supportsTvSearch?: boolean
  movieCategories?: number[] | null
  tvCategories?: number[] | null
  movieSearchParams?: string[] | null
  tvSearchParams?: string[] | null
}
/** `definitionId` is required for the 'Definition' protocol and rejected otherwise. */
export interface AddIndexerRequest {
  name: string
  protocol: IndexerProtocol
  baseUrl: string
  priority: number
  definitionId?: string | null
  /** Stored in the same save as the indexer; a refused credential adds nothing. */
  credential?: SetIndexerCredentialRequest | null
}

export interface IndexerSettings {
  minimumSeeders: number | null
  preferMagnet: boolean
  queryLimit: number | null
  grabLimit: number | null
  limitsUnit: 'Day'
  useFlareSolverr: boolean
}

/**
 * An administrator-chosen URL that publishes a JSON manifest of indexer entries. Cinomni ships with
 * none. A failed refresh is reported in the `lastRefresh*` fields, never as an HTTP error.
 */
export interface CatalogSource {
  id: string
  name: string
  url: string
  enabled: boolean
  createdAt: string
  lastRefreshedAt: string | null
  /** Null until the first refresh has been attempted. */
  lastRefreshSucceeded: boolean | null
  lastRefreshCode: string | null
  lastRefreshMessage: string | null
  entryCount: number
}

export interface AddCatalogSourceRequest {
  name: string
  url: string
}

export interface UpdateCatalogSourceRequest {
  name: string
  enabled: boolean
}

export interface IndexerCatalogEntry {
  /** Unique within its source only; `sourceId` + `key` identifies an entry. */
  key: string
  sourceId: string
  sourceName: string
  version: number
  name: string
  description: string
  protocol: IndexerProtocol
  releaseProtocol: ReleaseProtocol
  baseUrls: string[]
  requiresFlareSolverr: boolean
  installedIndexerId: string | null
  defaultPriority: number
  defaultSettings: IndexerSettings
}

export interface IndexerCatalogDraftRequest {
  name: string
  baseUrl: string
  priority: number
  settings: IndexerSettings
}

export interface IndexerTestResult {
  succeeded: boolean
  code: string
  message: string
  candidateCount: number
  durationMs: number
  testedAt: string
  /**
   * Whether the test searched with the indexer's stored session, when its definition declares a login
   * and a readable credential; null otherwise, because "authenticated" is not a meaningful question
   * for an indexer that needs no login. False means the sign-in failed and anything returned is what
   * the site shows an anonymous visitor.
   */
  authenticated: boolean | null
}

/** An uploaded indexer definition, as listed for an administrator — never its rules/content. */
export interface IndexerDefinitionSummary {
  id: string
  name: string
  schemaVersion: number
  contentHash: string
  createdAt: string
}
export interface UploadIndexerDefinitionRequest {
  name: string
  rawContent: string
}
/**
 * `sampleResponseBody`/`sampleRequestUrl` are optional: omit both for format-only validation, or
 * supply both to preview what the definition would extract from a sample response. Never issues a
 * real HTTP request either way.
 */
export interface ValidateIndexerDefinitionRequest {
  rawContent: string
  sampleResponseBody?: string | null
  sampleRequestUrl?: string | null
}
/**
 * One declared field rule of one result row that produced no value. This is what tells a rule that
 * failed apart from a site that genuinely reports nothing: a size of 0 looks identical either way,
 * so a definition can only be debugged when the failures are named.
 */
export interface DefinitionFieldIssue {
  /** Zero-based position of the row within the sample response. */
  rowIndex: number
  /** The definition's own field name: `title`, `downloadUrl`, `sizeBytes`, `seeders`, `publishedAt`. */
  field: string
  /** What the selector produced, truncated; null when it produced nothing at all. */
  rawValue: string | null
  code: string
  message: string
}
export interface ValidateIndexerDefinitionResponse {
  candidates: ReleaseCandidate[]
  fieldIssues: DefinitionFieldIssue[]
}

// --- Requests -----------------------------------------------------------------------------------
export type MediaRequestStatus = 'Pending' | 'Approved' | 'Rejected' | 'Available'
/** What a request is for. Part of the title's identity: TMDB numbers films and shows independently. */
export type MediaRequestKind = 'Movie' | 'Series'

export interface MediaRequest {
  id: string
  title: string
  year: number | null
  provider: string
  externalId: string
  kind: MediaRequestKind
  status: MediaRequestStatus
  requestedByUserId: string
  requestedByUsername: string
  workId: string | null
  decisionNote: string | null
  requestedAt: string
  decidedAt: string | null
}
export interface SubmitRequestBody {
  title: string
  year: number | null
  provider: string
  externalId: string
  kind: MediaRequestKind
}

// --- Notifications ------------------------------------------------------------------------------
export type NotificationSeverity = 'Info' | 'Success' | 'Warning' | 'Error'
export type NotificationChannelKind = 'Webhook' | 'Discord'

export interface AppNotification {
  id: string
  type: string
  severity: NotificationSeverity
  title: string
  body: string
  workId: string | null
  read: boolean
  createdAt: string
}
export interface NotificationChannel {
  id: string
  kind: NotificationChannelKind
  name: string
  target: string
  enabled: boolean
}
export interface AddChannelRequest {
  kind: NotificationChannelKind
  name: string
  target: string
}

// --- Platform health ----------------------------------------------------------------------------
/**
 * The readiness probe body. It is deliberately tiny on the server side because both probes are
 * anonymous: an orchestrator has no credentials, so a check's description or exception — which on
 * this installation would name a storage root or carry a connection string — is never emitted. A
 * name and a status is the whole contract, and the console must not imply it knows more.
 */
export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy'
export interface HealthCheckReport {
  name: string
  status: HealthStatus
}
export interface HealthReport {
  status: HealthStatus
  checks: HealthCheckReport[]
}

// --- Imports ------------------------------------------------------------------------------------
export type ImportJobState =
  | 'Pending'
  | 'Matching'
  | 'Deciding'
  | 'Operating'
  | 'Probing'
  | 'Registered'
  | 'Rejected'
  | 'Unmatched'
export type ImportFileMatchState = 'Planned' | 'Operated' | 'Probed' | 'Registered' | 'Failed' | 'Unresolved'
export type ImportOperationType = 'Hardlink' | 'Copy' | 'Move' | 'Rename' | 'Recycle' | 'Unknown'
export type ImportOperationState = 'Planned' | 'Executing' | 'Verified' | 'RolledBack' | 'Failed'
export type ImportMediaStreamKind = 'Video' | 'Audio' | 'Subtitle'

/**
 * One import job. `targetPath` and `assetId` describe the first file only: a season pack lands many,
 * so `fileCount` is what says whether the summary tells the whole story, and the per-file trail on
 * the detail is what does.
 */
export interface ImportJobSummary {
  id: string
  downloadTaskId: string
  intentId: string
  state: ImportJobState
  sourcePath: string
  targetPath: string | null
  assetId: string | null
  reason: string | null
  fileCount: number
}
export interface ImportFileMatch {
  seq: number
  sourcePath: string
  size: number
  targetPath: string | null
  assetId: string
  unitIds: string[]
  seasonNumber: number | null
  episodeNumbers: number[]
  state: ImportFileMatchState
  reason: string | null
}
export interface ImportMediaStream {
  index: number
  kind: ImportMediaStreamKind
  codec: string
  language: string | null
  width: number | null
  height: number | null
  channels: number | null
  isDefault: boolean
  isForced: boolean
}
/** The ffprobe result carried on a job. Distinct from the Library module's playback-facing streams. */
export interface ImportMediaInfo {
  container: string
  durationSeconds: number
  bitrate: number
  streams: ImportMediaStream[]
}
export interface ImportFileOperation {
  seq: number
  type: ImportOperationType
  from: string
  to: string
  state: ImportOperationState
  verified: boolean
}
export interface ImportJobDetail {
  job: ImportJobSummary
  files: ImportFileMatch[]
  /** Null until probing completes. */
  mediaInfo: ImportMediaInfo | null
  operations: ImportFileOperation[]
  history: StateHistoryEntry[]
}

export type PathRepairOutcome = 'Repairable' | 'Repaired' | 'Announced' | 'Blocked' | 'Absent'
export interface PathRepairEntry {
  assetId: string
  from: string
  to: string
  outcome: PathRepairOutcome
  /** Sidecar files named from the video's stem that move with it. */
  sidecars: number
}
/** What a repair run would do, computed without touching anything. */
export interface PathRepairReport {
  entries: PathRepairEntry[]
  repairable: number
  blocked: number
}
/** 202 Accepted: the pass runs on the command queue, so this is a receipt, not a result. */
export interface PathRepairRunAccepted {
  runId: string
}

// --- Decision (profiles) ------------------------------------------------------------------------
export interface AcquisitionProfile {
  id: string
  name: string
  minFormatScore: number
  cutoffRank: number
  upgradesAllowed: boolean
}
export interface UpgradePolicyRequest {
  cutoffRank: number
  upgradesAllowed: boolean
}

// --- Downloads (priorities) ---------------------------------------------------------------------
/** Keys are file indexes: System.Text.Json writes a dictionary key as a string, so the client must too. */
export interface SetDownloadPrioritiesRequest {
  priorities: Record<string, FilePriorityLevel>
}

// --- Acquisition (detail) -----------------------------------------------------------------------
export type AttemptState = 'Started' | 'Downloading' | 'Imported' | 'FailedDownload' | 'FailedImport'
/** What a tried release was, as the indexer described it when it was chosen. */
export interface AttemptRelease {
  title: string
  indexerName: string
  seeders: number | null
  leechers: number | null
}
export interface AcquisitionAttempt {
  id: string
  ordinal: number
  releaseGuid: string
  state: AttemptState
  startedAt: string
  closedAt: string | null
  failureReason: string | null
  /** Null for an attempt opened before this was recorded. */
  release: AttemptRelease | null
}
export interface AcquisitionIntentDetail {
  intent: AcquisitionIntentSummary
  attempts: AcquisitionAttempt[]
  history: StateHistoryEntry[]
}

// --- Operations spine ---------------------------------------------------------------------------
/** Command lifecycle states, keyed by name in the queue snapshot. */
export type CommandState = 'Queued' | 'Running' | 'Completed' | 'Failed'
export interface QueueSnapshot {
  outboxPending: number
  /** Age of the oldest pending message: a backlog that is large differs from one that is stuck. */
  oldestOutboxAgeSeconds: number
  commandsByState: Partial<Record<CommandState, number>>
}
export interface ScheduledJob {
  name: string
  commandType: string
  intervalSeconds: number
  /** Null if the job has never fired. Whether that run succeeded is not persisted anywhere. */
  lastRun: string | null
  nextDue: string
  enabled: boolean
}
export interface FailedCommand {
  id: string
  commandType: string
  attempts: number
  maxAttempts: number
  lastAttemptAt: string | null
  error: string | null
}
export interface RetentionWindows {
  outboxRetentionSeconds: number
  completedCommandRetentionSeconds: number
  failedCommandRetentionSeconds: number
  batchSize: number
  intervalSeconds: number
}

// --- Operations spine (settings store) -----------------------------------------------------------
export type SettingKind = 'Text' | 'Secret' | 'Number' | 'Boolean' | 'Duration' | 'List' | 'Enum'
/**
 * Where the effective value came from, in the precedence `SettingsView` already walks:
 * `environment` outranks everything and is never editable here, `database` is an operator override
 * stored through this same endpoint, `file` is `appsettings`/env-file configuration, `default` is
 * the `SettingDefinition`'s own fallback.
 */
export type SettingSource = 'environment' | 'database' | 'file' | 'default'
export interface OperationsSetting {
  key: string
  kind: SettingKind
  isSecret: boolean
  configurationPath: string
  /** Null when `isSecret` — a secret's raw value is never sent to the client. */
  value: string | null
  isSet: boolean
  source: SettingSource
  /** `Setting.Version` if a row exists, else 0. Send this back as `expectedVersion` on the next write. */
  version: number
  defaultValue: string | null
  /** The closed set an `Enum` key takes; absent or null for any other kind. */
  allowedValues?: string[] | null
  /** Bounds of a `Number` key, when it has them. */
  minValue?: number | null
  maxValue?: number | null
}
export interface UpdateSettingRequest {
  /** `null` clears the override (reverts to file/default); a string sets it. */
  value: string | null
  expectedVersion: number
}

// --- System -------------------------------------------------------------------------------------
/**
 * What this installation says about the build it is running.
 *
 * `commit` and `buildDate` are null on a build nothing stamped — every development build, and any
 * image built without the build arguments. That is a fact about the build, not an error and not an
 * unknown, so a caller leaves the row out rather than rendering a blank one. The backend also
 * reports `commit` as null when the build metadata after `+` is not plausibly a revision, so an
 * `informationalVersion` carrying a suffix while `commit` is null is expected, not a contradiction.
 */
export interface SystemInfo {
  /** The plain three-part number, e.g. `1.2.3`. */
  version: string
  /**
   * The full version including any prerelease and build metadata, e.g. `1.2.3-beta.1+a1b2c3d`. This
   * is the one that identifies a build without ambiguity; on an unstamped build it equals `version`.
   */
  informationalVersion: string
  commit: string | null
  buildDate: string | null
}
