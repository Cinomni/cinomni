import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { monitoringApi } from '@/api/endpoints'
import type { MonitoredTarget, WorkKind } from '@/api/types'
import { errorMessage } from '@/lib/api'
import { formatAirDate, formatEpisodeCode, isUnaired } from '@/lib/format'
import { workPath } from '@/lib/routes'
import { Alert } from '@/ui/Alert'
import { Badge, type Tone } from '@/ui/Badge'
import { Card } from '@/ui/Card'
import { DataTable, type Column } from '@/ui/DataTable'
import { EmptyState } from '@/ui/EmptyState'
import { ErrorState } from '@/ui/ErrorState'
import { LoadingBlock } from '@/ui/LoadingBlock'
import { Segmented } from '@/ui/Segmented'
import { SearchIcon } from '@/ui/icons'

/**
 * Row counts the limit control offers. The endpoint clamps `limit` to 500 server-side, so 500 is
 * both the largest useful step and the point past which raising the control further would do
 * nothing — offering a bigger step would promise paging the endpoint cannot do.
 */
const LIMIT_STEPS = [50, 100, 200, 500] as const
const DEFAULT_LIMIT = 100

/**
 * What kind of catalog unit the target is, with the SxxEyy code for a season or episode. Falls
 * back to the bare word when a season/episode number is unexpectedly absent, rather than printing
 * a broken code.
 */
function typeLabel(target: MonitoredTarget): string {
  switch (target.kind) {
    case 'Movie':
      return 'Movie'
    case 'Series':
      return 'Series'
    case 'Season':
      return target.seasonNumber == null ? 'Season' : `Season · ${formatEpisodeCode(target.seasonNumber)}`
    case 'Episode':
      return target.seasonNumber == null || target.episodeNumber == null
        ? 'Episode'
        : `Episode · ${formatEpisodeCode(target.seasonNumber, target.episodeNumber)}`
  }
}

/** The row's title, falling back to its type/code when the target itself carries no title. */
function titleFor(target: MonitoredTarget): string {
  return target.title ?? typeLabel(target)
}

/**
 * `MonitoredTarget.kind` is a TargetKind ('Movie' | 'Season' | 'Episode' | 'Series'), not the
 * WorkKind ('Movie' | 'Series') `workPath()` needs — but the mapping is exact rather than a guess.
 * Only a Series work has seasons, episodes or a series-level target; only a Movie work has a Movie
 * target. So 'Movie' -> WorkKind 'Movie' and every other target kind -> WorkKind 'Series' is sound,
 * not a fallback of convenience.
 */
function workKindFor(target: MonitoredTarget): WorkKind {
  return target.kind === 'Movie' ? 'Movie' : 'Series'
}

interface RowStatus {
  tone: Tone
  label: string
}

/**
 * Presents the row's state without re-deciding anything: `monitored` and `airDate` are exactly
 * what the backend sent. An episode that has simply not aired yet is not a failure to acquire, so
 * it gets a calmer tone than a title that should already be on disk — the same distinction
 * `EpisodeRow` draws for a season's episode list.
 */
function statusFor(target: MonitoredTarget): RowStatus {
  if (!target.monitored) return { tone: 'neutral', label: 'Not monitored' }
  if (isUnaired(target.airDate)) return { tone: 'neutral', label: 'Unaired' }
  return { tone: 'warning', label: 'Missing' }
}

const columns: Column<MonitoredTarget>[] = [
  { key: 'title', header: 'Title', render: titleFor },
  {
    key: 'type',
    header: 'Type',
    render: (target) => <span className="font-mono text-xs text-faint">{typeLabel(target)}</span>,
  },
  { key: 'airDate', header: 'Air date', render: (target) => formatAirDate(target.airDate) ?? '—' },
  {
    key: 'status',
    header: 'Status',
    render: (target) => {
      const status = statusFor(target)
      return <Badge tone={status.tone}>{status.label}</Badge>
    },
  },
]

/**
 * The console's Wanted page: monitored titles the library does not have yet, across every
 * collection this account can see.
 *
 * The list is a snapshot, not a queue — nothing here is ordered by priority or schedules a search.
 * `monitoringApi.missing` has no offset paging, only a `limit` the server clamps at 500, so this
 * page offers a bigger single page instead of faking pages it cannot deliver, and says so plainly
 * when a page comes back exactly full: a full page is the one honest signal there may be more.
 */
export function WantedPage() {
  const navigate = useNavigate()
  const [limit, setLimit] = useState<number>(DEFAULT_LIMIT)

  const missing = useQuery({
    queryKey: ['monitoring', 'missing', limit],
    queryFn: () => monitoringApi.missing(limit),
  })

  const atLimit = missing.data !== undefined && missing.data.length === limit

  return (
    <div className="space-y-4">
      <Card as="section" className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="font-medium text-fg">Wanted</h2>
            <p className="mt-1 text-sm text-muted">
              Monitored titles that are not in the library yet. An unaired episode is listed as
              unaired, not as missing — there is nothing to acquire until it broadcasts.
            </p>
          </div>
          <Segmented
            label="Rows to show"
            options={LIMIT_STEPS.map((step) => ({ value: String(step), label: String(step) }))}
            value={String(limit)}
            onChange={(value) => setLimit(Number(value))}
          />
        </div>

        {missing.isPending ? (
          <LoadingBlock size="md" label="Loading wanted titles" />
        ) : missing.isError ? (
          <ErrorState
            title="Wanted list could not be loaded"
            message={errorMessage(missing.error)}
            onRetry={() => void missing.refetch()}
          />
        ) : (
          <>
            {atLimit && (
              <Alert tone="info">
                Showing {missing.data.length} — there may be more. Raise "Rows to show" to see further wanted
                titles; this list does not page beyond what the server returns.
              </Alert>
            )}
            <DataTable
              columns={columns}
              rows={missing.data}
              rowKey={(target) => target.id}
              caption="Monitored titles missing from the library"
              onRowClick={(target) => navigate(workPath({ id: target.workId, kind: workKindFor(target) }))}
              empty={
                <EmptyState
                  icon={<SearchIcon className="size-9" />}
                  title="Nothing wanted"
                  description="Every monitored title is already in the library."
                />
              }
            />
          </>
        )}
      </Card>
    </div>
  )
}
