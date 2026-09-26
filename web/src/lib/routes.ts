import type { WorkKind } from '@/api/types'

/**
 * Where a work's detail page lives. A series has its own surface (seasons, episodes, per-episode
 * monitoring); routing one to the movie-shaped page would hide everything the slice added, so every
 * link site goes through here rather than hard-coding `/works/:id`.
 */
export function workPath(work: { id: string; kind: WorkKind }): string {
  return work.kind === 'Series' ? `/series/${work.id}` : `/works/${work.id}`
}
