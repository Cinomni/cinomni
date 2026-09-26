import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { catalogApi, libraryApi, monitoringApi, playbackApi } from '@/api/endpoints'
import type { PlaybackProgressView, Work } from '@/api/types'

/** How many unfinished rows to ask for; titles repeat across episodes, so more than the rail shows. */
export const IN_PROGRESS_LIMIT = 40

export interface ContinueItem {
  work: Work
  assetId: string
  positionTicks: number
  /** 0 when the player never reported a runtime; then only the position can be shown. */
  durationTicks: number
}

/** Newest first. Work ids are UUIDv7, so their string order is creation order. */
export function newestFirst(works: readonly Work[]): Work[] {
  return [...works].sort((a, b) => b.id.localeCompare(a.id))
}

/**
 * What the viewer has started and not finished, one entry per title. The server's in-progress query
 * already chose the rows (unfinished, visible to this viewer, most recent first); this only pairs each
 * with its work and keeps the most recent row of a series whose episodes appear more than once.
 */
export function continueWatching(
  works: readonly Work[],
  progress: readonly PlaybackProgressView[],
): ContinueItem[] {
  const workById = new Map(works.map((work) => [work.id, work]))
  const seen = new Set<string>()
  const items: ContinueItem[] = []
  for (const row of progress) {
    const work = row.workId ? workById.get(row.workId) : undefined
    if (!work || seen.has(work.id)) continue
    seen.add(work.id)
    items.push({
      work,
      assetId: row.assetId,
      positionTicks: row.positionTicks,
      durationTicks: row.durationTicks,
    })
  }
  return items
}

/** How far through the item the viewer is, 0..1, or null when the runtime is unknown. */
export function progressFraction(item: Pick<ContinueItem, 'positionTicks' | 'durationTicks'>): number | null {
  return item.durationTicks > 0 ? Math.min(1, item.positionTicks / item.durationTicks) : null
}

/**
 * The title the home page opens on: the newest one that can be watched right now and has a backdrop to
 * show, then the newest with a backdrop at all, then simply the newest. A presentation choice over the
 * catalog's own answer — nothing here ranks or recommends.
 */
export function pickFeatured(works: readonly Work[]): Work | null {
  const newest = newestFirst(works)
  const watchable = (work: Work) => work.hasAsset || work.availableEpisodeCount > 0
  return (
    newest.find((work) => work.backdropUrl && watchable(work)) ??
    newest.find((work) => work.backdropUrl) ??
    newest[0] ??
    null
  )
}

export function useHomeData(isAdmin: boolean) {
  // Same key as the activity page and the search overlay: one read of the visible catalog.
  const works = useQuery({ queryKey: ['works', null], queryFn: () => catalogApi.list() })
  const assets = useQuery({ queryKey: ['assets', 'all'], queryFn: () => libraryApi.assets() })

  const progress = useQuery({
    queryKey: ['playback', 'in-progress'],
    queryFn: ({ signal }) => playbackApi.inProgress(IN_PROGRESS_LIMIT, signal),
  })

  // Same key as the calendar page.
  const calendar = useQuery({ queryKey: ['monitoring', 'calendar'], queryFn: () => monitoringApi.calendar() })

  // Administrator-only on the API; a member's client never asks. Same key as the console page.
  const importList = useQuery({
    queryKey: ['catalog', 'import-list'],
    queryFn: catalogApi.importList,
    enabled: isAdmin,
  })

  const continueItems = useMemo(
    () => continueWatching(works.data ?? [], progress.data ?? []),
    [works.data, progress.data],
  )

  return { works, assets, progress, calendar, importList, continueItems }
}
