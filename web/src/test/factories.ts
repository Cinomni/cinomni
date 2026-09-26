import { vi } from 'vitest'
import type { CurrentUser, Episode, MonitoredTarget, MonitoredTargetTree, Season, Work } from '@/api/types'
import type { AuthContextValue } from '@/auth/context'

// Wire-shape builders for the tests. Each one is a complete, valid API object; a test overrides only
// the fields it is actually about, so adding a field to a DTO never breaks an unrelated test.

export function aWork(overrides: Partial<Work> = {}): Work {
  return {
    id: 'work-1',
    kind: 'Movie',
    title: 'A Title',
    year: 2020,
    status: 'Released',
    hasAsset: false,
    posterUrl: null,
    backdropUrl: null,
    metadataSnapshotId: null,
    seasonCount: 0,
    episodeCount: 0,
    availableEpisodeCount: 0,
    collectionId: 'collection-1',
    awaitingMetadata: false,
    externalIds: [],
    overview: null,
    runtimeMinutes: null,
    genres: [],
    posterSmallUrl: null,
    backdropSmallUrl: null,
    backdropLargeUrl: null,
    ...overrides,
  }
}

export function aSeason(overrides: Partial<Season> = {}): Season {
  return {
    id: 'season-1',
    workId: 'work-1',
    number: 1,
    title: null,
    airDate: '2020-01-05',
    expectedEpisodeCount: 2,
    posterUrl: null,
    posterSmallUrl: null,
    ...overrides,
  }
}

export function anEpisode(overrides: Partial<Episode> = {}): Episode {
  return {
    id: 'episode-1',
    seasonId: 'season-1',
    workId: 'work-1',
    seasonNumber: 1,
    number: 1,
    absoluteNumber: null,
    title: 'Pilot',
    airDate: '2020-01-05',
    airDateTime: null,
    runtimeMinutes: 45,
    hasAsset: false,
    ...overrides,
  }
}

export function aTarget(overrides: Partial<MonitoredTarget> = {}): MonitoredTarget {
  return {
    id: 'target-1',
    workId: 'work-1',
    kind: 'Episode',
    monitored: false,
    mode: 'All',
    isMissing: true,
    targetRef: 'episode-1',
    parentId: null,
    seasonNumber: 1,
    episodeNumber: 1,
    absoluteNumber: null,
    airDate: null,
    title: null,
    missingCount: 0,
    totalCount: 0,
    ...overrides,
  }
}

export function aTargetTree(overrides: Partial<MonitoredTargetTree> = {}): MonitoredTargetTree {
  return {
    root: aTarget({ id: 'target-root', kind: 'Series', monitored: true, targetRef: 'work-1', seasonNumber: null }),
    seasons: [],
    ...overrides,
  }
}

/**
 * The signed-in account, as `GET /api/identity/me` projects it. Tests that only care about a role or
 * a permission override that one field; everything else stays a complete, valid object, so a field
 * added to the DTO does not break every test that happens to mention a user.
 */
export function aCurrentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  const isAdministrator = overrides.isAdministrator ?? false
  return {
    id: 'u-1',
    username: 'someone',
    isAdministrator,
    role: isAdministrator ? 'Administrator' : 'Member',
    permissions: {
      canRequest: true,
      requestsAutoApproved: isAdministrator,
      openRequestLimit: null,
      contentCeiling: null,
      contentCeilingRegion: null,
      contentCeilingApplies: false,
    },
    twoFactorEnabled: false,
    ...overrides,
  }
}

/** An authenticated context value whose callbacks are all spies. Pass the account it is signed in as. */
export function anAuthContext(user: CurrentUser, overrides: Partial<AuthContextValue> = {}): AuthContextValue {
  return {
    status: 'authed',
    user,
    login: vi.fn(),
    completeTwoFactor: vi.fn(),
    setup: vi.fn(),
    logout: vi.fn(),
    refreshUser: vi.fn(),
    retrySession: vi.fn(),
    ...overrides,
  }
}
