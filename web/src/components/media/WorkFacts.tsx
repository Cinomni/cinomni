import type { Work } from '@/api/types'
import { formatRuntime } from '@/lib/format'
import { availabilityLabel, availabilityOf, mediaStateFromAvailability } from '@/lib/status'
import { MediaMetadata } from './MediaMetadata'
import { MediaStatus } from './MediaStatus'

/** A ticket line holds a couple of genres; the provider's full list reads as noise at that size. */
const EYEBROW_GENRES = 2

/**
 * The ticket line of a title: kind, year, runtime or seasons, its first genres, and release status when
 * it is not the usual.
 */
export function WorkEyebrow({ work }: { work: Work }) {
  const isSeries = work.kind === 'Series'
  return (
    <MediaMetadata
      items={[
        isSeries ? 'Series' : 'Movie',
        work.year,
        !isSeries && formatRuntime(work.runtimeMinutes),
        isSeries && work.seasonCount > 0 && `${work.seasonCount} ${work.seasonCount === 1 ? 'season' : 'seasons'}`,
        work.genres.slice(0, EYEBROW_GENRES).join(', '),
        work.status === 'Announced' && 'Announced',
      ]}
    />
  )
}

/** Where the title stands in this library, in words: "In library", "Partly in library · 8 of 10 episodes". */
export function WorkAvailability({ work, className }: { work: Work; className?: string }) {
  const availability = availabilityOf(work)
  const detail =
    work.kind === 'Series' && work.episodeCount > 0
      ? ` · ${work.availableEpisodeCount} of ${work.episodeCount} episodes`
      : ''
  return (
    <MediaStatus
      state={mediaStateFromAvailability[availability]}
      label={`${availabilityLabel[availability]}${detail}`}
      className={className}
    />
  )
}
