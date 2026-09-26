import { useCallback, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { catalogApi, libraryApi, monitoringApi, playbackApi } from '@/api/endpoints'
import type {
  MediaAssetSummary,
  MonitoredTarget,
  MonitoredTargetTree,
  MonitoringMode,
  Season,
} from '@/api/types'
import { ApiError } from '@/lib/api'

/**
 * Query keys for a series, as a single hierarchy: `['series', workId, …]`.
 *
 * The hierarchy is the point. Elsewhere in the app keys are ad-hoc inline arrays, so the only way to
 * refresh anything is to invalidate a whole page. Here every season hangs off
 * `['series', workId, 'season', n]`, so toggling one season invalidates exactly its own subtree —
 * its episodes and its watched flags — and leaves the other twenty seasons in cache.
 */
export const seriesKeys = {
  /** Everything about one series; invalidating this re-reads the whole page. */
  all: (workId: string) => ['series', workId] as const,
  seasons: (workId: string) => ['series', workId, 'seasons'] as const,
  targets: (workId: string) => ['series', workId, 'targets'] as const,
  assets: (workId: string) => ['series', workId, 'assets'] as const,
  nextUp: (workId: string) => ['series', workId, 'next-up'] as const,
  /** One season's subtree — the prefix of the two keys below. */
  season: (workId: string, seasonNumber: number) => ['series', workId, 'season', seasonNumber] as const,
  episodes: (workId: string, seasonNumber: number) =>
    ['series', workId, 'season', seasonNumber, 'episodes'] as const,
  seasonProgress: (workId: string, seasonNumber: number) =>
    ['series', workId, 'season', seasonNumber, 'progress'] as const,
}

/** How often a still-materialising series is re-read while the outbox fans work out. */
const FAN_OUT_POLL_INTERVAL_MS = 2_000

/**
 * Upper bound on that polling. A cascading policy opens one target per episode asynchronously, so the
 * page cannot simply refetch once after a fixed delay — it would render a half-built hierarchy. It
 * also cannot poll forever: a stuck relay must not turn every open tab into a load generator.
 */
const FAN_OUT_POLL_TIMEOUT_MS = 60_000

export interface FanOutWindow {
  /** Re-open the window: something was just queued and its effects are still landing. */
  extend: () => void
  /** The refetch interval to use while `isSettled` is false, or false once it is (or time ran out). */
  pollWhile: (isSettled: boolean) => number | false
}

/**
 * A bounded "work has been queued and is still fanning out" window. `refetchInterval` is re-evaluated
 * after every fetch, so a tree that finishes materialising stops its own polling and the deadline is
 * only the backstop for one that never does. One window is shared by every query on a series page, so
 * applying a policy or queuing a refresh wakes them all.
 */
export function useFanOutWindow(): FanOutWindow {
  const deadlineRef = useRef(Date.now() + FAN_OUT_POLL_TIMEOUT_MS)
  const extend = useCallback(() => {
    deadlineRef.current = Date.now() + FAN_OUT_POLL_TIMEOUT_MS
  }, [])
  const pollWhile = useCallback(
    (isSettled: boolean): number | false =>
      isSettled || Date.now() > deadlineRef.current ? false : FAN_OUT_POLL_INTERVAL_MS,
    [],
  )
  return { extend, pollWhile }
}

/** 404 is the API's "this work has no targets yet", not a failure worth retrying or surfacing. */
async function orNullOn404<T>(load: () => Promise<T>): Promise<T | null> {
  try {
    return await load()
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null
    throw error
  }
}

/**
 * The series' seasons. Polls while the list is still empty: adding a series only queues the metadata
 * refresh, and the season/episode tree lands a few seconds later.
 */
export function useSeriesSeasons(workId: string, fanOut: FanOutWindow) {
  return useQuery({
    queryKey: seriesKeys.seasons(workId),
    queryFn: () => catalogApi.seasons(workId),
    refetchInterval: (query) => fanOut.pollWhile((query.state.data?.length ?? 0) > 0),
  })
}

/** One season's episodes. Loaded only once its accordion is open — a long-running show has hundreds. */
export function useSeasonEpisodes(workId: string, seasonNumber: number, enabled: boolean) {
  return useQuery({
    queryKey: seriesKeys.episodes(workId, seasonNumber),
    queryFn: () => catalogApi.episodes(workId, seasonNumber),
    enabled,
  })
}

/**
 * Every asset of the series, indexed by the catalog unit it plays. One work-scoped call covers every
 * season, which is why the episode rows do not each resolve their own file. A multi-episode file is a
 * single asset with several unit links, so it legitimately appears under more than one key.
 */
export function useSeriesAssets(workId: string) {
  return useQuery({
    queryKey: seriesKeys.assets(workId),
    queryFn: async () => indexAssetsByUnit(await libraryApi.assets(workId)),
  })
}

function indexAssetsByUnit(assets: readonly MediaAssetSummary[]): ReadonlyMap<string, MediaAssetSummary> {
  const byUnit = new Map<string, MediaAssetSummary>()
  for (const asset of assets) {
    for (const unitId of asset.unitIds) {
      // An upgraded asset is superseded by the active one; first Active wins, otherwise first seen.
      const existing = byUnit.get(unitId)
      if (!existing || (existing.state !== 'Active' && asset.state === 'Active')) byUnit.set(unitId, asset)
    }
  }
  return byUnit
}

/** Watched flags for one season, keyed by asset id. Lives under the season key with its episodes. */
export function useSeasonProgress(workId: string, seasonNumber: number, assetIds: readonly string[]) {
  return useQuery({
    queryKey: [...seriesKeys.seasonProgress(workId, seasonNumber), assetIds.join(',')],
    queryFn: async () => {
      const progress = await playbackApi.progressFor(assetIds)
      return new Map(progress.map((row) => [row.assetId, row]))
    },
    enabled: assetIds.length > 0,
  })
}

/** The episode to resume. Null for a movie, and 204 (undefined) once a series is fully watched. */
export function useNextUp(workId: string) {
  return useQuery({
    queryKey: seriesKeys.nextUp(workId),
    queryFn: async () => (await playbackApi.nextUp(workId)) ?? null,
  })
}

/**
 * The work's monitored-target tree plus the policy mutation, together because they share one fan-out
 * window: applying a cascading policy queues N targets through the outbox, and the tree is what has
 * to keep re-reading until they have all landed.
 */
export function useSeriesMonitoring(workId: string, expectedSeasonCount: number, fanOut: FanOutWindow) {
  const queryClient = useQueryClient()

  const tree = useQuery({
    queryKey: seriesKeys.targets(workId),
    queryFn: () => orNullOn404(() => monitoringApi.targetsForWork(workId)),
    refetchInterval: (query) => fanOut.pollWhile(isTreeComplete(query.state.data, expectedSeasonCount)),
  })

  const applyPolicy = useMutation({
    mutationFn: (mode: MonitoringMode) => monitoringApi.applyPolicy(workId, mode),
    onSuccess: async () => {
      fanOut.extend()
      await queryClient.invalidateQueries({ queryKey: seriesKeys.all(workId) })
    },
  })

  return { tree, applyPolicy }
}

/** A tree is done materialising once its root and a node per known season are present. */
function isTreeComplete(tree: MonitoredTargetTree | null | undefined, expectedSeasonCount: number): boolean {
  if (tree == null) return false
  return tree.root !== null && tree.seasons.length >= expectedSeasonCount
}

/**
 * Toggle one episode target, applied to the cached tree first so the switch answers immediately and
 * rolls back if the server refuses. On settle the season's own subtree is re-read, together with the
 * work-scoped tree that carries the rollup counters the season and series headers show.
 */
export function useSetEpisodeMonitored(workId: string, seasonNumber: number) {
  const queryClient = useQueryClient()
  const targetsKey = seriesKeys.targets(workId)

  return useMutation({
    mutationFn: ({ targetId, monitored }: { targetId: string; monitored: boolean }) =>
      monitoringApi.setMonitored(targetId, monitored),
    onMutate: async ({ targetId, monitored }) => {
      await queryClient.cancelQueries({ queryKey: targetsKey })
      const previous = queryClient.getQueryData<MonitoredTargetTree | null>(targetsKey)
      if (previous) {
        queryClient.setQueryData(targetsKey, withTargetMonitored(previous, targetId, monitored))
      }
      return { previous }
    },
    onError: (_error, _variables, context) => {
      if (context?.previous !== undefined) queryClient.setQueryData(targetsKey, context.previous)
    },
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: seriesKeys.season(workId, seasonNumber) }),
        queryClient.invalidateQueries({ queryKey: targetsKey }),
      ])
    },
  })
}

/** Toggle a whole branch (a season, or the series root). Cascades server-side, so the tree is re-read. */
export function useSetSubtreeMonitored(workId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ targetId, monitored }: { targetId: string; monitored: boolean }) =>
      monitoringApi.setSubtreeMonitored(targetId, monitored),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: seriesKeys.targets(workId) }),
  })
}

/** Manual, season-scoped search. Clears the season's cooldown and runs the sweep server-side. */
export function useSeasonSearch(workId: string) {
  return useMutation({
    mutationFn: (seasonNumber: number) => monitoringApi.searchSeason(workId, seasonNumber),
  })
}

const withMonitored = (target: MonitoredTarget, monitored: boolean): MonitoredTarget => ({
  ...target,
  monitored,
})

/** A copy of the tree with one target's flag flipped — nothing in the cache is mutated in place. */
function withTargetMonitored(
  tree: MonitoredTargetTree,
  targetId: string,
  monitored: boolean,
): MonitoredTargetTree {
  return {
    root: tree.root,
    seasons: tree.seasons.map((season) => ({
      target: season.target.id === targetId ? withMonitored(season.target, monitored) : season.target,
      episodes: season.episodes.map((episode) =>
        episode.id === targetId ? withMonitored(episode, monitored) : episode,
      ),
    })),
  }
}

/** A season paired with the monitoring state of its own target and of its episodes. */
export interface SeasonWithTargets {
  season: Season
  target: MonitoredTarget | null
  /** Episode targets of this season, indexed by the catalog episode id they watch (`targetRef`). */
  targetByEpisodeId: ReadonlyMap<string, MonitoredTarget>
}

/** Joins the catalog seasons with the monitoring tree so each accordion gets one self-contained prop. */
export function joinSeasonsWithTargets(
  seasons: readonly Season[],
  tree: MonitoredTargetTree | null | undefined,
): SeasonWithTargets[] {
  const nodeByNumber = new Map((tree?.seasons ?? []).map((node) => [node.target.seasonNumber, node]))
  return seasons.map((season) => {
    const node = nodeByNumber.get(season.number)
    return {
      season,
      target: node?.target ?? null,
      targetByEpisodeId: new Map((node?.episodes ?? []).map((episode) => [episode.targetRef, episode])),
    }
  })
}
