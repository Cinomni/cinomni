import { ApiError, api, postOnUnload } from '@/lib/api'
import type {
  AcquisitionIntentDetail,
  AcquisitionIntentSummary,
  AcquisitionProfile,
  AddChannelRequest,
  FailedCommand,
  ImportJobDetail,
  ImportJobSummary,
  PathRepairReport,
  PathRepairRunAccepted,
  QueueSnapshot,
  RetentionWindows,
  ScheduledJob,
  SetDownloadPrioritiesRequest,
  SetIndexerCapabilitiesRequest,
  SetIndexerCredentialRequest,
  UpgradePolicyRequest,
  AddIndexerRequest,
  IndexerDefinitionSummary,
  UploadIndexerDefinitionRequest,
  ValidateIndexerDefinitionRequest,
  ValidateIndexerDefinitionResponse,
  AddMovieRequest,
  AddSeriesRequest,
  AppNotification,
  Collection,
  CollectionAccessMode,
  CreateCollectionRequest,
  CreateUserRequest,
  CurrentUser,
  HealthReport,
  NotificationChannel,
  DownloadTaskDetail,
  DownloadTaskSummary,
  TunnelEgressStatus,
  Episode,
  IndexerSummary,
  CatalogSource,
  AddCatalogSourceRequest,
  UpdateCatalogSourceRequest,
  IndexerCatalogDraftRequest,
  IndexerCatalogEntry,
  IndexerSettings,
  IndexerTestResult,
  InteractiveSearchResult,
  LoginResponse,
  ManualSelection,
  ReleaseBlock,
  ReleaseBlockList,
  MediaAssetDetail,
  MediaAssetSummary,
  MediaRequest,
  MediaRequestStatus,
  MetadataProviderName,
  MetadataCandidate,
  MetadataSnapshot,
  MediaKind,
  CalendarEntry,
  ImportListView,
  MonitoredTarget,
  MonitoredTargetTree,
  MonitoringMode,
  NextUpEpisode,
  OperationsSetting,
  PlaybackProgressView,
  PlaybackSessionDetail,
  PlaybackTicket,
  HardwareReport,
  ProgressBody,
  RefreshMetadataRequest,
  ReleaseEvaluation,
  RequestPlaybackBody,
  Season,
  SessionIssued,
  SetupRequired,
  SystemInfo,
  TwoFactorEnrollment,
  SubmitRequestBody,
  SubtitleSearchSummary,
  UpdateSettingRequest,
  UserAccount,
  ContentRatingLadder,
  UserPermissions,
  UserRole,
  Work,
  WorkKind,
  WorkFacets,
  WorkPage,
  WorkPageQuery,
} from './types'

// --- Platform health ----------------------------------------------------------------------------
export const healthApi = {
  /**
   * Readiness answers 503 when a dependency is unhealthy, and that response body is the one worth
   * reading — it names the check that failed. Degraded deliberately stays 200: a missing sidecar or
   * a storage root running low still leaves an installation that browses and plays.
   */
  ready: (signal?: AbortSignal) => api.getTolerating<HealthReport>('/health/ready', [503], signal),
}

// --- System -------------------------------------------------------------------------------------
export const systemApi = {
  /**
   * Which build this installation is running. Authenticated but not administrator-only on purpose:
   * the point of a version line is that a member reporting a problem can say what they are on.
   *
   * The process reads its own build identity once at startup and it cannot change while it runs, so
   * callers treat the answer as permanently fresh rather than re-reading it.
   */
  info: (signal?: AbortSignal) => api.get<SystemInfo>('/api/system/info', signal),
}

// --- Identity -----------------------------------------------------------------------------------
export const identityApi = {
  setupRequired: () => api.get<SetupRequired>('/api/identity/setup-required'),
  setup: (username: string, password: string) =>
    api.post<{ userId: string }>('/api/identity/setup', { username, password }),
  login: (username: string, password: string) =>
    api.post<LoginResponse>('/api/identity/login', { username, password }),
  /**
   * The second step of a sign-in. `code` carries either a six-digit authenticator code or one of the
   * recovery codes — the same field, deliberately, so never narrow it to digits on the way in.
   *
   * Every way this can fail answers 401 `identity.invalid_two_factor_code`: expired challenge, spent
   * challenge, wrong code, attempts exhausted. That is one error on purpose — telling them apart
   * would tell an attacker which half of the credential they already hold — so do not try to infer
   * the cause from it.
   */
  loginTwoFactor: (challenge: string, code: string) =>
    api.post<SessionIssued>('/api/identity/login/two-factor', { challenge, code }),
  me: () => api.get<CurrentUser>('/api/identity/me'),
  logout: () => api.post<void>('/api/identity/logout'),
  users: () => api.get<UserAccount[]>('/api/identity/users'),
  createUser: (body: CreateUserRequest) => api.post<{ userId: string }>('/api/identity/users', body),
  setUserRole: (id: string, role: UserRole) => api.put<void>(`/api/identity/users/${id}/role`, { role }),
  setUserPermissions: (id: string, permissions: UserPermissions) =>
    api.put<void>(`/api/identity/users/${id}/permissions`, permissions),
  setUserDisabled: (id: string, disabled: boolean) =>
    api.put<void>(`/api/identity/users/${id}/disabled`, { disabled }),

  /**
   * Begins enrolment. The password is asked for again because turning the second factor on is a
   * change to what protects the account, not a preference — a borrowed session is not enough.
   *
   * This turns nothing on. The account keeps signing in exactly as before until `confirmTwoFactor`.
   */
  enrollTwoFactor: (password: string) =>
    api.post<TwoFactorEnrollment>('/api/identity/two-factor/enroll', { password }),
  /**
   * Confirms the enrolment, which is what actually turns the second factor on, and the only moment
   * the recovery codes exist anywhere they can be read. They are not retrievable afterwards.
   */
  confirmTwoFactor: (code: string) =>
    api.post<{ recoveryCodes: string[] }>('/api/identity/two-factor/confirm', { code }),
  /**
   * Turns it off, and revokes every other session of the account — afterwards it is protected by a
   * password alone, so a session left open on another device matters more than it did.
   */
  disableTwoFactor: (password: string, code: string) =>
    api.post<void>('/api/identity/two-factor/disable', { password, code }),
}

// --- Catalog ------------------------------------------------------------------------------------
export const catalogApi = {
  /** Removes a work; the other modules let go of it on their own. Files go only when asked. */
  remove: (workId: string, deleteFiles: boolean) =>
    api.del<void>(`/api/catalog/works/${workId}?deleteFiles=${deleteFiles}`),
  importList: () => api.get<ImportListView>('/api/catalog/import-list'),
  refreshImportList: () => api.post<void>('/api/catalog/import-list/refresh'),
  // What comes back is already narrowed to the collections this account may browse.
  list: (collectionId?: string) =>
    api.get<Work[]>(`/api/catalog/works${collectionId ? `?collection=${collectionId}` : ''}`),
  /**
   * One page of the same list, filtered and ordered on the server, with the total that matches. The
   * server clamps `limit` (max 500) and answers 400 for a kind, availability or sort it does not name.
   */
  page: (query: WorkPageQuery, offset: number, limit: number, signal?: AbortSignal) => {
    const params = new URLSearchParams({ offset: String(offset), limit: String(limit) })
    if (query.collectionId) params.set('collection', query.collectionId)
    if (query.kind) params.set('kind', query.kind)
    if (query.q) params.set('q', query.q)
    if (query.availability) params.set('availability', query.availability)
    if (query.genre) params.set('genre', query.genre)
    if (query.sort) params.set('sort', query.sort)
    return api.get<WorkPage>(`/api/catalog/works/page?${params.toString()}`, signal)
  },
  facets: (collectionId?: string, kind?: WorkKind, signal?: AbortSignal) => {
    const params = new URLSearchParams()
    if (collectionId) params.set('collection', collectionId)
    if (kind) params.set('kind', kind)
    const query = params.toString()
    return api.get<WorkFacets>(`/api/catalog/works/facets${query ? `?${query}` : ''}`, signal)
  },
  get:(id: string) => api.get<Work>(`/api/catalog/works/${id}`),
  /**
   * Whether a work with this external identity is already in the catalog, narrowed to what the caller
   * may browse. A 404 is the ANSWER here, not a failure — no work carries that identity, or none the
   * caller may see, which read identically by design — so it comes back as null rather than throwing.
   * Every other status still throws: a lookup that failed does not prove a title is absent.
   */
  getByExternalId: async (
    provider: MetadataProviderName,
    value: string,
    signal?: AbortSignal,
    kind?: WorkKind,
  ): Promise<Work | null> => {
    // The kind narrows the answer: a provider can number films and shows independently, so the same
    // id can belong to one of each.
    const query = kind ? `?kind=${encodeURIComponent(kind)}` : ''
    try {
      return await api.get<Work>(
        `/api/catalog/works/by-external/${encodeURIComponent(provider)}/${encodeURIComponent(value)}${query}`,
        signal,
      )
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null
      throw error
    }
  },
  add: (body: AddMovieRequest) => api.post<{ workId: string }>('/api/catalog/works', body),
  addSeries: (body: AddSeriesRequest) => api.post<{ workId: string }>('/api/catalog/series', body),
  seasons: (workId: string) => api.get<Season[]>(`/api/catalog/works/${workId}/seasons`),
  episodes: (workId: string, seasonNumber: number) =>
    api.get<Episode[]>(`/api/catalog/works/${workId}/seasons/${seasonNumber}/episodes`),
  episode: (workId: string, episodeId: string) =>
    api.get<Episode>(`/api/catalog/works/${workId}/episodes/${episodeId}`),

  contentRatings: () => api.get<ContentRatingLadder>('/api/catalog/content-ratings'),
  collections: () => api.get<Collection[]>('/api/catalog/collections'),
  createCollection: (body: CreateCollectionRequest) =>
    api.post<{ collectionId: string }>('/api/catalog/collections', body),
  setCollectionAccessMode: (id: string, accessMode: CollectionAccessMode) =>
    api.put<void>(`/api/catalog/collections/${id}/access-mode`, { accessMode }),
  collectionGrants: (id: string) => api.get<string[]>(`/api/catalog/collections/${id}/grants`),
  grantCollection: (id: string, userId: string) =>
    api.put<void>(`/api/catalog/collections/${id}/grants/${userId}`),
  revokeCollection: (id: string, userId: string) =>
    api.del<void>(`/api/catalog/collections/${id}/grants/${userId}`),
  moveWork: (workId: string, collectionId: string) =>
    api.put<void>(`/api/catalog/works/${workId}/collection`, { collectionId }),
}

// --- Metadata -----------------------------------------------------------------------------------
export const metadataApi = {
  search: (term: string, year: number | null, kind: MediaKind = 'Movie') => {
    const params = new URLSearchParams({ term, kind })
    if (year != null) params.set('year', String(year))
    return api.get<MetadataCandidate[]>(`/api/metadata/search?${params.toString()}`)
  },
  refresh: (body: RefreshMetadataRequest) => api.post<void>('/api/metadata/refresh', body),
  snapshot: (id: string) => api.get<MetadataSnapshot>(`/api/metadata/snapshots/${id}`),
  selectArtwork: (snapshotId: string, artworkId: string) =>
    api.post<void>(`/api/metadata/snapshots/${snapshotId}/artwork/${artworkId}/select`),
}

// --- Monitoring ---------------------------------------------------------------------------------
export const monitoringApi = {
  applyPolicy: (workId: string, mode: MonitoringMode) =>
    api.put<{ targetId: string }>(`/api/monitoring/works/${workId}/policy`, { mode }),
  setMonitored: (targetId: string, monitored: boolean) =>
    api.put<void>(`/api/monitoring/targets/${targetId}/monitored`, { monitored }),
  // A whole branch at once: a series root cascades to its seasons and their episodes.
  setSubtreeMonitored: (targetId: string, monitored: boolean) =>
    api.put<void>(`/api/monitoring/targets/${targetId}/subtree-monitored`, { monitored }),
  targets: () => api.get<MonitoredTarget[]>('/api/monitoring/targets'),
  /**
   * Monitored targets still absent from the library — the wanted list. `limit` is clamped server-side
   * to at most 500, so a caller asking for more silently gets 500 rather than an error; the page must
   * not present the result as complete when it comes back full.
   */
  calendar: (from?: string, to?: string) => {
    const params = new URLSearchParams()
    if (from) params.set('from', from)
    if (to) params.set('to', to)
    const query = params.toString()
    return api.get<CalendarEntry[]>(`/api/monitoring/calendar${query ? `?${query}` : ''}`)
  },
  missing: (limit = 100, workId?: string) => {
    const params = new URLSearchParams({ limit: String(limit) })
    if (workId) params.set('workId', workId)
    return api.get<MonitoredTarget[]>(`/api/monitoring/targets/missing?${params.toString()}`)
  },
  targetForWork: (workId: string) => api.get<MonitoredTarget>(`/api/monitoring/works/${workId}/target`),
  // The tree form of the route above: the root with its rollup counters, then a node per season.
  targetsForWork: (workId: string) => api.get<MonitoredTargetTree>(`/api/monitoring/works/${workId}/targets`),
  searchSeason: (workId: string, seasonNumber: number) =>
    api.post<void>(`/api/monitoring/works/${workId}/seasons/${seasonNumber}/search`),
  /** Searches for one target now: its work for a movie, its season for an episode. */
  searchTarget: (targetId: string) => api.post<void>(`/api/monitoring/targets/${targetId}/search`),
}

// --- Library ------------------------------------------------------------------------------------
export const libraryApi = {
  assets: (workId?: string) =>
    api.get<MediaAssetSummary[]>(`/api/library/assets/${workId ? `?workId=${workId}` : ''}`),
  asset: (id: string) => api.get<MediaAssetDetail>(`/api/library/assets/${id}`),
}

// --- Subtitles ----------------------------------------------------------------------------------
export const subtitlesApi = {
  forAsset: (assetId: string) => api.get<SubtitleSearchSummary[]>(`/api/subtitles/assets/${assetId}`),
}

// --- Playback -----------------------------------------------------------------------------------
export const playbackApi = {
  start: (body: RequestPlaybackBody) => api.post<PlaybackTicket>('/api/playback/sessions', body),
  progress: (sessionId: string, body: ProgressBody) =>
    api.post<void>(`/api/playback/sessions/${sessionId}/progress`, body),
  stop: (sessionId: string) => api.post<void>(`/api/playback/sessions/${sessionId}/stop`),
  /** A subtitle track as WebVTT text, from the URL the ticket gave for it. */
  subtitles: (url: string, signal?: AbortSignal) => api.text(url, signal),
  /** What the last hardware probe found on this host (administrators). */
  hardware: (signal?: AbortSignal) => api.get<HardwareReport>('/api/playback/hardware', signal),
  /** Runs the hardware probe again — real test encodes and decodes — and returns what it found. */
  probeHardware: () => api.post<HardwareReport>('/api/playback/hardware/probe'),
  /** Records the subtitle track the viewer switched to (null: off), so the title remembers it. */
  chooseSubtitle: (sessionId: string, subtitleStreamIndex: number | null) =>
    api.put<void>(`/api/playback/sessions/${sessionId}/subtitle`, { subtitleStreamIndex }),
  /** The stop sent as the page goes away: it must survive the unload and keep the token out of the URL. */
  stopOnUnload: (sessionId: string) => postOnUnload(`/api/playback/sessions/${sessionId}/stop`),
  session: (id: string) => api.get<PlaybackSessionDetail>(`/api/playback/sessions/${id}`),
  resume: (assetId: string) => api.get<PlaybackProgressView | undefined>(`/api/playback/progress/${assetId}`),
  // Batch form: the watched flags of a whole season in one call. Repeated query parameter.
  progressFor: (assetIds: readonly string[]) => {
    const params = new URLSearchParams()
    for (const assetId of assetIds) params.append('assetIds', assetId)
    return api.get<PlaybackProgressView[]>(`/api/playback/progress?${params.toString()}`)
  },
  nextUp: (workId: string) => api.get<NextUpEpisode | undefined>(`/api/playback/next-up/${workId}`),
  /**
   * "Continue watching": the caller's started, unfinished items, most recent first, already narrowed to
   * the titles they may still see. The server caps `limit` at 50.
   */
  inProgress: (limit = 20, signal?: AbortSignal) =>
    api.get<PlaybackProgressView[]>(`/api/playback/in-progress?limit=${limit}`, signal),
}

// --- Downloads ----------------------------------------------------------------------------------
export const downloadsApi = {
  list: () => api.get<DownloadTaskSummary[]>('/api/downloads/'),
  get: (id: string) => api.get<DownloadTaskDetail>(`/api/downloads/${id}`),
  pause: (id: string) => api.post<void>(`/api/downloads/${id}/pause`),
  // `force` is the override the PauseAndAlert policy allows and Block refuses. Without it, resuming a
  // network-held download answers 409 `downloads.network_held` — a refusal with a reason, not a fault.
  resume: (id: string, force = false) =>
    api.post<void>(`/api/downloads/${id}/resume${force ? '?force=true' : ''}`),
  remove: (id: string, deleteFiles = false) =>
    api.post<void>(`/api/downloads/${id}/remove?deleteFiles=${deleteFiles}`),
  tunnel: () => api.get<TunnelEgressStatus>('/api/downloads/tunnel'),
  setPriorities: (id: string, body: SetDownloadPrioritiesRequest) =>
    api.put<void>(`/api/downloads/${id}/priorities`, body),
}

// --- Acquisition --------------------------------------------------------------------------------
export const acquisitionApi = {
  intents: () => api.get<AcquisitionIntentSummary[]>('/api/acquisition/intents'),
  /** One intent with every attempt it made and the transitions between them. */
  intent: (id: string) => api.get<AcquisitionIntentDetail>(`/api/acquisition/intents/${id}`),
  /** Reopens an exhausted goal, or leaves a searching one as it is; the search itself is Monitoring's. */
  retry: (id: string) => api.post<AcquisitionIntentSummary>(`/api/acquisition/intents/${id}/retry`),
}

// --- Imports ------------------------------------------------------------------------------------
export const importsApi = {
  list: () => api.get<ImportJobSummary[]>('/api/imports/'),
  get: (id: string) => api.get<ImportJobDetail>(`/api/imports/${id}`),
  /** A dry run: what a repair pass would move, computed without touching a single file. */
  pathRepairPreview: () => api.get<PathRepairReport>('/api/imports/path-repair'),
  /** Queues the pass as a recoverable command and answers 202 with a receipt, not a result. */
  runPathRepair: () => api.post<PathRepairRunAccepted>('/api/imports/path-repair'),
}

// --- Operations ---------------------------------------------------------------------------------
export const operationsApi = {
  queue: () => api.get<QueueSnapshot>('/api/operations/queue'),
  jobs: () => api.get<ScheduledJob[]>('/api/operations/jobs'),
  /** Newest failure first. The server caps `limit` at 200 whatever is asked for. */
  failedCommands: (limit = 50) => api.get<FailedCommand[]>(`/api/operations/commands/failed?limit=${limit}`),
  retention: () => api.get<RetentionWindows>('/api/operations/retention'),
  /** The settings store's editable catalogue: only the properties that already had a named key. */
  settings: () => api.get<OperationsSetting[]>('/api/operations/settings'),
  /** `value: null` clears the override; a string sets it. Answers the freshly recomputed setting. */
  updateSetting: (key: string, body: UpdateSettingRequest) =>
    api.put<OperationsSetting>(`/api/operations/settings/${encodeURIComponent(key)}`, body),
}

// --- Decision -----------------------------------------------------------------------------------
export const decisionApi = {
  /**
   * Runs a search for one monitored target now and returns every candidate with its verdict and
   * reasons. It selects nothing: the choice is the caller's, through `grab`.
   */
  search: (targetId: string) =>
    api.post<InteractiveSearchResult>(`/api/decision/targets/${targetId}/search`),
  /** Acquires one evaluated release by hand, whatever the profile made of it. */
  grab: (evaluationId: string) =>
    api.post<ManualSelection>(`/api/decision/evaluations/${evaluationId}/grab`),
  block: (evaluationId: string, reason: string) =>
    api.post<ReleaseBlock>(`/api/decision/evaluations/${evaluationId}/block`, { reason }),
  blocks: () => api.get<ReleaseBlockList>('/api/decision/blocks'),
  unblock: (id: string) => api.del<void>(`/api/decision/blocks/${id}`),
  /** Everything ever evaluated for a target, newest first. */
  evaluations: (targetId: string) =>
    api.get<ReleaseEvaluation[]>(`/api/decision/targets/${targetId}/evaluations`),
  profiles: () => api.get<AcquisitionProfile[]>('/api/decision/profiles'),
  /**
   * The cutoff rank a title is good enough at, and whether upgrades may be searched at all. These are
   * the only two knobs the API exposes on a profile; everything else about it is fixed at seed time.
   */
  setUpgradePolicy: (id: string, body: UpgradePolicyRequest) =>
    api.put<void>(`/api/decision/profiles/${id}/upgrade-policy`, body),
}

// --- Discovery ----------------------------------------------------------------------------------
export const discoveryApi = {
  indexers: () => api.get<IndexerSummary[]>('/api/discovery/indexers'),
  indexerCatalog: () => api.get<IndexerCatalogEntry[]>('/api/discovery/indexer-catalog'),
  installCatalogIndexer: (sourceId: string, key: string, body: IndexerCatalogDraftRequest) =>
    api.post<{ indexerId: string }>(
      `/api/discovery/indexer-catalog/${encodeURIComponent(sourceId)}/${encodeURIComponent(key)}/install`,
      body,
    ),
  testCatalogIndexer: (sourceId: string, key: string, body: IndexerCatalogDraftRequest) =>
    api.post<IndexerTestResult>(
      `/api/discovery/indexer-catalog/${encodeURIComponent(sourceId)}/${encodeURIComponent(key)}/test`,
      body,
    ),
  catalogSources: () => api.get<CatalogSource[]>('/api/discovery/indexer-catalog/sources'),
  /** The first refresh is attempted before this returns; its outcome is in the `lastRefresh*` fields. */
  addCatalogSource: (body: AddCatalogSourceRequest) =>
    api.post<CatalogSource>('/api/discovery/indexer-catalog/sources', body),
  refreshCatalogSource: (id: string) =>
    api.post<CatalogSource>(`/api/discovery/indexer-catalog/sources/${encodeURIComponent(id)}/refresh`),
  updateCatalogSource: (id: string, body: UpdateCatalogSourceRequest) =>
    api.put<CatalogSource>(`/api/discovery/indexer-catalog/sources/${encodeURIComponent(id)}`, body),
  /** Indexers already installed from the source stay installed. */
  removeCatalogSource: (id: string) =>
    api.del<void>(`/api/discovery/indexer-catalog/sources/${encodeURIComponent(id)}`),
  addIndexer: (body: AddIndexerRequest) => api.post<{ indexerId: string }>('/api/discovery/indexers', body),
  setIndexerSettings: (id: string, body: IndexerSettings) =>
    api.put<void>(`/api/discovery/indexers/${id}/settings`, body),
  testIndexer: (id: string) => api.post<IndexerTestResult>(`/api/discovery/indexers/${id}/test`),
  /** Omitting a field keeps the backend default rather than clearing it — send the whole shape. */
  setCapabilities: (id: string, body: SetIndexerCapabilitiesRequest) =>
    api.put<void>(`/api/discovery/indexers/${id}/capabilities`, body),
  /** Write-only: there is no route that reads a credential back, by design. */
  setCredential: (id: string, body: SetIndexerCredentialRequest) =>
    api.put<void>(`/api/discovery/indexers/${id}/credential`, body),
  clearCredential: (id: string) => api.del<void>(`/api/discovery/indexers/${id}/credential`),
  setIndexerEnabled: (id: string, enabled: boolean) =>
    api.put<void>(`/api/discovery/indexers/${id}/enabled`, { enabled }),
  setIndexerPriority: (id: string, priority: number) =>
    api.put<void>(`/api/discovery/indexers/${id}/priority`, { priority }),
  deleteIndexer: (id: string) => api.del<void>(`/api/discovery/indexers/${id}`),
  definitions: () => api.get<IndexerDefinitionSummary[]>('/api/discovery/indexer-definitions'),
  uploadDefinition: (body: UploadIndexerDefinitionRequest) =>
    api.post<{ definitionId: string }>('/api/discovery/indexer-definitions', body),
  /** Dry-run only: never issues a real HTTP request, even with a sample response supplied. */
  validateDefinition: (body: ValidateIndexerDefinitionRequest) =>
    api.post<ValidateIndexerDefinitionResponse>('/api/discovery/indexer-definitions/validate', body),
}

// --- Requests -----------------------------------------------------------------------------------
export const requestsApi = {
  // `mine` is only a narrowing hint: the API scopes a non-administrator to their own requests anyway.
  list: (status?: MediaRequestStatus, mine = false, limit = 100) => {
    const params = new URLSearchParams({ limit: String(limit) })
    if (status) params.set('status', status)
    if (mine) params.set('mine', 'true')
    return api.get<MediaRequest[]>(`/api/requests/?${params.toString()}`)
  },
  pendingCount: () => api.get<{ count: number }>('/api/requests/pending-count'),
  submit: (body: SubmitRequestBody) => api.post<{ requestId: string }>('/api/requests/', body),
  approve: (id: string) => api.post<void>(`/api/requests/${id}/approve`),
  reject: (id: string, reason: string | null) => api.post<void>(`/api/requests/${id}/reject`, { reason }),
}

// --- Notifications ------------------------------------------------------------------------------
export const notificationsApi = {
  list: (unreadOnly = false, limit = 50) =>
    api.get<AppNotification[]>(`/api/notifications/?unreadOnly=${unreadOnly}&limit=${limit}`),
  unreadCount: () => api.get<{ count: number }>('/api/notifications/unread-count'),
  markRead: (id: string) => api.post<void>(`/api/notifications/${id}/read`),
  markAllRead: () => api.post<void>('/api/notifications/read-all'),
  channels: () => api.get<NotificationChannel[]>('/api/notifications/channels'),
  addChannel: (body: AddChannelRequest) => api.post<{ channelId: string }>('/api/notifications/channels', body),
  setChannelEnabled: (id: string, enabled: boolean) =>
    api.put<void>(`/api/notifications/channels/${id}/enabled`, { enabled }),
  deleteChannel: (id: string) => api.del<void>(`/api/notifications/channels/${id}`),
}
