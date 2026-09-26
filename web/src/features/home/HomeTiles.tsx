import { Link } from 'react-router'
import type { CalendarEntry, Work } from '@/api/types'
import { PosterArt, ProgressEdge } from '@/components/media/MediaPoster'
import { cn } from '@/lib/cn'
import { formatDuration, formatEpisodeCode, ticksToSeconds } from '@/lib/format'
import { workPath } from '@/lib/routes'
import { PlayIcon } from '@/ui/icons'
import { progressFraction, type ContinueItem } from './useHomeData'

/**
 * A landscape still for "Continue watching": the backdrop (or the poster, cropped) with the title set
 * on it and where playback stops. The whole tile resumes — that is the only thing this row is for; the
 * title's page is one step further, from its own poster anywhere else.
 */
export function ContinueTile({ item }: { item: ContinueItem }) {
  const { work } = item
  const position = formatDuration(ticksToSeconds(item.positionTicks))
  const fraction = progressFraction(item)
  const backdrop = work.backdropSmallUrl ?? work.backdropUrl
  return (
    <Link
      to={`/watch/${item.assetId}`}
      aria-label={`Resume ${work.title} from ${position}`}
      className="group block rounded-media outline-none"
    >
      <div className="relative aspect-video overflow-hidden rounded-media bg-surface ring-2 ring-transparent ring-offset-2 ring-offset-bg group-focus-visible:ring-accent">
        {backdrop ? (
          <img
            src={backdrop}
            alt=""
            loading="lazy"
            decoding="async"
            className="size-full object-cover transition-transform duration-200 ease-out-quint group-hover:scale-[1.03]"
          />
        ) : (
          <PosterArt work={work} className="object-[center_25%]" />
        )}
        <span aria-hidden="true" className="pointer-events-none absolute inset-0 rounded-media ring-1 ring-inset ring-white/8" />
        <div className="absolute inset-0 bg-linear-to-t from-scrim/90 via-scrim/20 to-transparent" />
        <span
          aria-hidden="true"
          className="absolute left-1/2 top-1/2 grid size-12 -translate-x-1/2 -translate-y-1/2 place-items-center rounded-full bg-accent text-on-accent opacity-0 transition-opacity duration-150 group-hover:opacity-100 group-focus-visible:opacity-100"
        >
          <PlayIcon className="ml-0.5 size-5" />
        </span>
        <div className="absolute inset-x-3 bottom-3">
          <p className="truncate text-card font-semibold text-fg">{work.title}</p>
          <p className="mt-0.5 flex items-center gap-2 text-meta tabular-nums text-fg/75">
            {/* Without a known runtime, the marquee line short: this is where you left off. */}
            {fraction == null && <span aria-hidden="true" className="h-0.75 w-6 rounded-full bg-accent" />}
            Resume at {position}
          </p>
        </div>
        {fraction != null && <ProgressEdge value={fraction} label={`${work.title} progress`} />}
      </div>
    </Link>
  )
}

/** A calendar date (`YYYY-MM-DD`) read field by field, so it never shifts a day west of Greenwich. */
function localDate(airDate: string): Date {
  const [year, month, day] = airDate.split('-').map(Number)
  return new Date(year, month - 1, day)
}

function dayLabel(airDate: string, today: Date): string {
  const date = localDate(airDate)
  const diff = Math.round((date.getTime() - new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime()) / 86_400_000)
  if (diff === 0) return 'Today'
  if (diff === 1) return 'Tomorrow'
  return date.toLocaleDateString(undefined, { weekday: 'short' })
}

/**
 * One airing as a ticket stub: the day large on the left, the show and episode on the right. Upcoming
 * items are about *when*, so the date — not the artwork — leads.
 */
export function UpcomingStub({ entry, work, today }: { entry: CalendarEntry; work: Work | undefined; today: Date }) {
  const date = localDate(entry.airDate)
  const code =
    entry.seasonNumber != null && entry.episodeNumber != null
      ? formatEpisodeCode(entry.seasonNumber, entry.episodeNumber)
      : null
  const isToday = dayLabel(entry.airDate, today) === 'Today'
  return (
    <Link
      to={workPath({ id: entry.workId, kind: entry.kind === 'Movie' ? 'Movie' : 'Series' })}
      className="group flex h-full items-stretch gap-3 rounded-media bg-surface p-2 pr-4 outline-none transition-colors hover:bg-elevated focus-visible:ring-2 focus-visible:ring-accent"
    >
      <span className="h-18 w-12 shrink-0 overflow-hidden rounded-media bg-elevated">
        {work && <PosterArt work={work} />}
      </span>
      <span className="flex min-w-0 flex-1 flex-col justify-center border-l border-dashed border-line pl-3">
        <span className={cn('text-label uppercase', isToday ? 'text-accent' : 'text-faint')}>
          {dayLabel(entry.airDate, today)} · {date.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}
        </span>
        <span className="mt-1 truncate text-card text-fg group-hover:text-accent-strong">{entry.workTitle}</span>
        <span className="truncate text-meta text-muted">
          {[code, entry.title].filter(Boolean).join(' · ') || 'New episode'}
        </span>
      </span>
    </Link>
  )
}
