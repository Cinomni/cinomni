import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { catalogApi, monitoringApi } from '@/api/endpoints'
import type { CalendarEntry, Work } from '@/api/types'
import { PageHeader } from '@/components/PageHeader'
import { MediaStatus } from '@/components/media/MediaStatus'
import { PosterArt } from '@/components/media/MediaPoster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/cn'
import { formatEpisodeCode } from '@/lib/format'
import { workPath } from '@/lib/routes'
import { Alert } from '@/ui/Alert'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { CalendarIcon } from '@/ui/icons'
import { Skeleton } from '@/ui/Skeleton'

/** The calendar route caps its answer here; a full page may not be the whole window. */
const CALENDAR_PAGE_LIMIT = 500

function labelFor(entry: CalendarEntry): string {
  if (entry.kind === 'Episode' && entry.seasonNumber != null && entry.episodeNumber != null) {
    const code = formatEpisodeCode(entry.seasonNumber, entry.episodeNumber)
    return entry.title ? `${code} · ${entry.title}` : code
  }
  return entry.kind === 'Movie' ? 'Movie' : entry.title ?? entry.kind
}

/** A calendar date (`YYYY-MM-DD`) read field by field, so it never shifts a day west of Greenwich. */
function localDate(airDate: string): Date {
  const [year, month, day] = airDate.split('-').map(Number)
  return new Date(year, month - 1, day)
}

function daysFromToday(airDate: string, today: Date): number {
  const start = new Date(today.getFullYear(), today.getMonth(), today.getDate())
  return Math.round((localDate(airDate).getTime() - start.getTime()) / 86_400_000)
}

function relativeDay(offset: number, date: Date): string {
  if (offset === 0) return 'Today'
  if (offset === 1) return 'Tomorrow'
  if (offset === -1) return 'Yesterday'
  return date.toLocaleDateString(undefined, { weekday: 'long' })
}

/**
 * Upcoming and recent airings as an agenda, one block per published date. The window and which titles
 * appear are the server's decision; this page only presents them — the date leads, because *when* is
 * the whole point of the page.
 */
export function CalendarPage() {
  const calendar = useQuery({
    queryKey: ['monitoring', 'calendar'],
    queryFn: () => monitoringApi.calendar(),
  })
  // Only for the artwork beside each airing; without it the agenda still reads fine.
  const works = useQuery({ queryKey: ['works', null], queryFn: () => catalogApi.list() })
  const workById = useMemo(() => new Map((works.data ?? []).map((work) => [work.id, work])), [works.data])

  const groups = new Map<string, CalendarEntry[]>()
  for (const entry of calendar.data ?? []) {
    const rows = groups.get(entry.airDate) ?? []
    rows.push(entry)
    groups.set(entry.airDate, rows)
  }
  const today = new Date()

  return (
    <>
      <PageHeader
        title="Upcoming"
        subtitle="Episodes with a known air date in the next two weeks. Movies have no air date here, so they are not listed; neither is a title you cannot see."
      />

      {calendar.isPending ? (
        <div role="status" aria-label="Loading the calendar" className="space-y-6">
          {Array.from({ length: 3 }, (_, index) => (
            <div key={index} className="space-y-3">
              <Skeleton className="h-5 w-32" />
              <Skeleton className="h-16" />
            </div>
          ))}
        </div>
      ) : calendar.isError ? (
        <ErrorState
          title="Calendar could not be loaded"
          message={errorMessage(calendar.error)}
          onRetry={() => void calendar.refetch()}
        />
      ) : calendar.data.length === 0 ? (
        <EmptyState
          icon={<CalendarIcon className="size-9" />}
          title="Nothing airing"
          description="No monitored episode has an air date in this window. Monitor a series and its next episodes appear here."
        />
      ) : (
        <div className="space-y-10">
          {calendar.data.length === CALENDAR_PAGE_LIMIT && (
            <Alert tone="info">Showing {CALENDAR_PAGE_LIMIT} airings. There may be more outside this page.</Alert>
          )}
          {[...groups.entries()].map(([date, rows]) => {
            const day = localDate(date)
            const offset = daysFromToday(date, today)
            return (
              <section key={date} aria-label={day.toLocaleDateString(undefined, { dateStyle: 'full' })} className="grid gap-4 sm:grid-cols-[8rem_1fr]">
                <header className="flex items-baseline gap-3 sm:block">
                  <p className={cn('text-label uppercase', offset === 0 ? 'text-accent' : 'text-faint')}>
                    {relativeDay(offset, day)}
                  </p>
                  <p className={cn('text-title tabular-nums', offset < 0 ? 'text-muted' : 'text-fg')}>
                    {day.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}
                  </p>
                </header>
                <ul className="divide-y divide-line-soft">
                  {rows.map((entry) => (
                    <AiringRow key={entry.id} entry={entry} work={workById.get(entry.workId)} />
                  ))}
                </ul>
              </section>
            )
          })}
        </div>
      )}
    </>
  )
}

function AiringRow({ entry, work }: { entry: CalendarEntry; work: Work | undefined }) {
  return (
    <li>
      <Link
        to={workPath({ id: entry.workId, kind: entry.kind === 'Movie' ? 'Movie' : 'Series' })}
        className="group -mx-3 flex items-center gap-4 rounded-control px-3 py-3 transition-colors hover:bg-surface"
      >
        <span className="h-14 w-10 shrink-0 overflow-hidden rounded-media bg-elevated">
          {work && <PosterArt work={work} />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-card font-semibold text-fg group-hover:text-accent-strong">
            {entry.workTitle}
          </span>
          <span className="mt-0.5 block truncate text-meta text-muted">{labelFor(entry)}</span>
        </span>
        {entry.isMissing ? (
          <MediaStatus state="missing" label="Missing" className="shrink-0" />
        ) : (
          <MediaStatus state="available" label="In library" className="shrink-0" />
        )}
      </Link>
    </li>
  )
}
